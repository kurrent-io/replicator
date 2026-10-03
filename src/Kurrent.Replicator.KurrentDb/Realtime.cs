using System.Text.Json;
using Kurrent.Replicator.KurrentDb.Internals;

namespace Kurrent.Replicator.KurrentDb;

sealed class SubscriptionDroppedEarlyException() : Exception("The subscription dropped before it was registered");

/// <summary>
/// Keeps StreamMetaCache current from a $all subscription. One serialized subscribe loop; drops are attributed to
/// the attempt (and token lease) that produced them. See the spec, "Realtime subscription".
/// </summary>
class Realtime {
    static ILog Log => LogProvider.GetLogger(typeof(Realtime));

    internal delegate Task<IDisposable> Subscribe(
        Func<ResolvedEvent, Task>                     onEvent,
        Action<SubscriptionDroppedReason, Exception?> onDropped,
        CallAuth                                      auth,
        CancellationToken                             ct
    );

    sealed class Attempt(CallAuth auth) {
        public CallAuth     Auth         { get; } = auth;
        public bool         Dropped      { get; set; }
        public IDisposable? Subscription { get; set; }
    }

    readonly Subscribe       _subscribe;
    readonly StreamMetaCache _cache;
    readonly GrpcAuthContext _auth;
    readonly object          _lock = new();

    Attempt? _published;
    Attempt? _pending;
    Task?    _subscribing;

    internal Action? OnUnpublishedUnderLock { get; set; }

    internal bool IsPublished {
        get { lock (_lock) return _published is { Dropped: false }; }
    }

    public Realtime(EventStoreClient client, StreamMetaCache cache, GrpcAuthContext auth)
        : this(
            async (onEvent, onDropped, a, ct) => await client.SubscribeToAllAsync(
                    FromAll.End,
                    (_, evt, _) => onEvent(evt),
                    subscriptionDropped: (_, reason, ex) => onDropped(reason, ex),
                    userCredentials: a.Credentials,
                    cancellationToken: ct
                )
                .ConfigureAwait(false),
            cache,
            auth
        ) { }

    internal Realtime(Subscribe subscribe, StreamMetaCache cache, GrpcAuthContext auth) {
        _subscribe = subscribe;
        _cache     = cache;
        _auth      = auth;
    }

    public Task Start(CancellationToken ct) => EnsureSubscribed(ct);

    Task EnsureSubscribed(CancellationToken ct) {
        Task loop;

        lock (_lock) {
            if (_published is { Dropped: false }) return Task.CompletedTask;

            if (_subscribing is null || _subscribing.IsCompleted) _subscribing = Task.Run(SubscribeLoop, CancellationToken.None);

            loop = _subscribing;
        }

        return loop.WaitAsync(ct);
    }

    async Task SubscribeLoop() {
        var failures = 0;

        while (true) {
            try {
                await _auth.Run(SubscribeOnce, _auth.Shutdown).ConfigureAwait(false);

                return;
            } catch (OperationCanceledException) when (_auth.Shutdown.IsCancellationRequested) {
                throw;
            } catch (Exception e) {
                Log.Warn("Realtime subscription failed; retrying ({Error})", AuthFailure.Describe(e));
                await Task.Delay(TokenGate.Backoff(failures++), _auth.Time, _auth.Shutdown).ConfigureAwait(false);
            }
        }
    }

    async Task<bool> SubscribeOnce(CallAuth auth, CancellationToken ct) {
        var attempt = new Attempt(auth);

        lock (_lock) _pending = attempt;

        IDisposable subscription;

        try {
            subscription = await _subscribe(re => HandleEvent(attempt, re), (reason, ex) => HandleDrop(attempt, reason, ex), auth, ct).ConfigureAwait(false);
        } catch {
            lock (_lock) {
                if (ReferenceEquals(_pending, attempt)) _pending = null;
            }

            throw;
        }

        lock (_lock) {
            if (ReferenceEquals(_pending, attempt)) _pending = null;

            if (attempt.Dropped) {
                subscription.Dispose();

                throw new SubscriptionDroppedEarlyException();
            }

            attempt.Subscription = subscription;
            _published           = attempt;
            // This loop makes no further attempts once published. Clearing it here (not when the task completes) means
            // a drop of this attempt that arrives before the loop task finishes starts a new loop instead of joining
            // this one, which would end without resubscribing.
            _subscribing = null;
            _cache.MarkLive();
        }

        Log.Info("Realtime subscription started");

        return true;
    }

    void HandleDrop(Attempt attempt, SubscriptionDroppedReason reason, Exception? exception) {
        if (reason == SubscriptionDroppedReason.Disposed) return;

        bool resubscribe;

        lock (_lock) {
            if (ReferenceEquals(attempt, _published)) {
                _auth.ReportFailure(attempt.Auth, exception);
                attempt.Dropped = true;
                _published      = null;
                _cache.MarkNotLive();
                OnUnpublishedUnderLock?.Invoke();
                resubscribe = true;
            }
            else if (ReferenceEquals(attempt, _pending)) {
                _auth.ReportFailure(attempt.Auth, exception);
                attempt.Dropped = true;
                resubscribe     = false;
            }
            else {
                return; // superseded attempt: ignore entirely
            }
        }

        if (exception != null)
            Log.Warn("Realtime subscription dropped: {Reason} ({Error})", reason, AuthFailure.Describe(exception));
        else
            Log.Warn("Realtime subscription dropped: {Reason}", reason);

        if (!resubscribe) return;

        _ = EnsureSubscribed(CancellationToken.None)
            .ContinueWith(
                t => {
                    if (t.Exception?.GetBaseException() is not OperationCanceledException)
                        Log.Error(t.Exception, "Realtime resubscription stopped");
                },
                TaskContinuationOptions.OnlyOnFaulted
            );
    }

    // Events are bound to the attempt that delivered them. A superseded attempt's in-flight callback could otherwise
    // write pre-gap data into the cache after a newer attempt's MarkLive cleared it. The current-attempt check and the
    // cache write happen under the same lock that publishes an attempt and calls MarkLive, so the two cannot
    // interleave: either the write lands before the newer attempt's MarkLive (and is cleared by it) or the attempt is
    // no longer current when the write would happen (and it is skipped).
    Task HandleEvent(Attempt attempt, ResolvedEvent re) {
        if (IsSystemEvent())
            return Task.CompletedTask;

        if (IsMetadataUpdate()) {
            var stream  = re.OriginalStreamId[2..];
            var meta    = JsonSerializer.Deserialize<StreamMetadata>(re.Event.Data.Span, MetaSerialization.StreamMetadataJsonSerializerOptions);
            var version = re.OriginalEventNumber.ToInt64();

            lock (_lock) {
                if (IsCurrent(attempt)) _cache.UpdateStreamMeta(stream, meta, version);
            }
        }
        else {
            lock (_lock) {
                if (IsCurrent(attempt)) _cache.UpdateStreamLastEventNumber(re.OriginalStreamId, re.OriginalEventNumber.ToInt64());
            }
        }

        return Task.CompletedTask;

        bool IsSystemEvent() => re.Event.EventType.StartsWith('$') && re.Event.EventType != Predefined.MetadataEventType;

        bool IsMetadataUpdate() => re.Event.EventType == Predefined.MetadataEventType;
    }

    // Caller holds _lock.
    bool IsCurrent(Attempt attempt) => !attempt.Dropped && (ReferenceEquals(attempt, _pending) || ReferenceEquals(attempt, _published));
}
