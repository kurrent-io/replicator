namespace Kurrent.Replicator.KurrentDb.Auth;

public sealed class ClientCredentialsTokenSource : IAccessTokenSource, IDisposable {
    static ILog Log => LogProvider.GetLogger(typeof(ClientCredentialsTokenSource));

    public static readonly TimeSpan RequestTimeout  = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan FallbackMargin  = TimeSpan.FromSeconds(30);

    sealed record Cached(string Value, DateTimeOffset ExpiresAt, TimeSpan Lifetime);

    readonly GrpcAuthOptions     _options;
    readonly string              _side;
    readonly TimeProvider        _time;
    readonly CancellationToken   _shutdown;
    readonly TokenEndpointClient _client;
    readonly TokenState          _state;
    readonly RateLimitedWarning  _fallbackWarning;
    readonly object              _lock = new();

    Cached?              _cached;
    Task<Cached>?        _inflight;
    DateTimeOffset?      _lastFailureAt;
    OAuthTokenException? _lastFailure;

    public ClientCredentialsTokenSource(GrpcAuthOptions options, string side, HttpMessageHandler handler, TimeProvider time, CancellationToken shutdown) {
        _options         = options;
        _side            = side;
        _time            = time;
        _shutdown        = shutdown;
        _client          = new TokenEndpointClient(options, handler, side);
        _state           = new TokenState(time);
        _fallbackWarning = new RateLimitedWarning(time, TimeSpan.FromSeconds(60));
    }

    public async ValueTask<AccessTokenLease> GetAccessToken(CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        Task<Cached> task;

        lock (_lock) {
            var now = _time.GetUtcNow();

            if (_cached is { } c && now < c.ExpiresAt - RefreshWindow(c)) return _state.Accept(c.Value);

            var usable = Usable(now);

            if (_inflight is null || _inflight.IsCompleted) {
                if (_lastFailureAt is { } failedAt && now - failedAt < FailureCooldown) {
                    if (usable != null) return _state.Accept(usable);

                    throw new OAuthTokenException(_lastFailure!.Message, _lastFailure);
                }

                _inflight = StartRefresh();
            }

            // The cached token is inside its refresh window but still usable: the refresh runs in the background
            // and this caller does not wait for it.
            if (usable != null) return _state.Accept(usable);

            task = _inflight;
        }

        try {
            var token = await task.WaitAsync(ct).ConfigureAwait(false);

            return _state.Accept(token.Value);
        } catch (OAuthTokenException) {
            string? fallback;
            lock (_lock) fallback = Usable(_time.GetUtcNow());

            if (fallback == null) throw;

            return _state.Accept(fallback);
        }
    }

    Task<Cached> StartRefresh() {
        var task = Task.Run(Refresh, CancellationToken.None);

        // Observes the exception of a refresh nobody awaits (background refresh inside the refresh window), and
        // reports the fallback to the still-usable cached token once, rate-limited.
        task.ContinueWith(
            t => {
                if (t.Exception?.InnerException is not OAuthTokenException) return;

                string? fallback;
                lock (_lock) fallback = Usable(_time.GetUtcNow());

                if (fallback != null && _fallbackWarning.ShouldLog())
                    Log.Warn("{Side}: token refresh failed; using the current token until it nears expiry", _side);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

        return task;
    }

    public void Invalidate(AccessTokenLease lease) {
        if (!_state.Invalidate(lease)) {
            Log.Debug("{Side}: ignoring stale token rejection report", _side);

            return;
        }

        lock (_lock) {
            if (_cached?.Value == lease.Value) _cached = null;
        }
    }

    public void ReportAccepted(AccessTokenLease lease) => _state.ReportAccepted(lease);

    TimeSpan RefreshWindow(Cached c) {
        var configured = TimeSpan.FromSeconds(Math.Max(0, _options.RefreshBeforeExpirySeconds));
        var half       = c.Lifetime / 2;

        return configured < half ? configured : half;
    }

    string? Usable(DateTimeOffset now)
        => _cached is { } c && now < c.ExpiresAt - FallbackMargin && !_state.IsRejected(c.Value) ? c.Value : null;

    async Task<Cached> Refresh() {
        using var timeout = new CancellationTokenSource(RequestTimeout, _time);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(_shutdown, timeout.Token);
        var       started = _time.GetUtcNow();

        try {
            var response = await _client.RequestToken(linked.Token).ConfigureAwait(false);
            var cached   = new Cached(response.AccessToken, started + response.Lifetime, response.Lifetime);

            lock (_lock) {
                _cached        = cached;
                _lastFailureAt = null;
                _lastFailure   = null;
            }

            Log.Info("{Side}: access token acquired from {Host}, expires at {ExpiresAt:O}", _side, _client.EndpointHost, cached.ExpiresAt);

            return cached;
        } catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) {
            throw;
        } catch (Exception e) {
            var failure = e switch {
                OAuthTokenException o      => o,
                OperationCanceledException => new OAuthTokenException($"{_side}: token request to {_client.EndpointHost} timed out after {RequestTimeout.TotalSeconds:0}s", e),
                _                          => new OAuthTokenException($"{_side}: token request to {_client.EndpointHost} failed: {e.GetType().Name}", e)
            };

            lock (_lock) {
                _lastFailureAt = _time.GetUtcNow();
                _lastFailure   = failure;
            }

            throw failure;
        }
    }

    public void Dispose() => _client.Dispose();
}
