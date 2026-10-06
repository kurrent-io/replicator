using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Shared.Contracts;
using Kurrent.Replicator.Shared.Logging;
using Kurrent.Replicator.Shared.Observe;
using Ubiquitous.Metrics;
using System.Text.Json;
using Kurrent.Replicator.KurrentDb.Internals;
using StreamAcl = EventStore.Client.StreamAcl;
using StreamMetadata = EventStore.Client.StreamMetadata;

namespace Kurrent.Replicator.KurrentDb;

public class GrpcEventWriter(EventStoreClient client, GrpcAuthContext auth) : IEventWriter {
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();

    readonly RateLimitedWarning _warn = new(auth.Time, TimeSpan.FromSeconds(60));
    int                         _outage;

    public GrpcEventWriter(EventStoreClient client) : this(client, GrpcAuthContext.None) { }

    public Task Start() => Task.CompletedTask;

    public async Task<long> WriteEvent(BaseProposedEvent proposedEvent, CancellationToken cancellationToken) {
        var task = proposedEvent switch {
            ProposedEvent p             => AppendEvent(p),
            ProposedDeleteStream delete => DeleteStream(delete.EventDetails.Stream),
            ProposedMetaEvent meta      => SetStreamMeta(meta),
            IgnoredEvent _              => Task.FromResult(-1L),
            _                           => throw new InvalidOperationException("Unknown proposed event type")
        };

        return
            task.IsCompleted
                ? task.Result
                : await Metrics.Measure(() => task, ReplicationMetrics.WritesHistogram, ReplicationMetrics.WriteErrorsCount).ConfigureAwait(false);

        async Task<long> AppendEvent(ProposedEvent p) {
            if (Log.IsDebugEnabled())
                Log.Debug(
                    "gRPC: Write event with id {Id} of type {Type} to {Stream} with original position {Position}",
                    p.EventDetails.EventId,
                    p.EventDetails.EventType,
                    p.EventDetails.Stream,
                    p.SourceLogPosition.EventPosition
                );

            var result = await Write(
                    (a, c) => client.AppendToStreamAsync(
                        proposedEvent.EventDetails.Stream,
                        StreamState.Any,
                        [Map(p)],
                        userCredentials: a.Credentials,
                        cancellationToken: c
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);

            return (long)result.LogPosition.CommitPosition;
        }

        async Task<long> DeleteStream(string stream) {
            if (Log.IsDebugEnabled())
                Log.Debug("Deleting stream {Stream}", stream);

            var result = await Write(
                    (a, c) => client.DeleteAsync(stream, StreamState.Any, userCredentials: a.Credentials, cancellationToken: c),
                    cancellationToken
                )
                .ConfigureAwait(false);

            return (long)result.LogPosition.CommitPosition;
        }

        // Written as a plain append of a $metadata event to $$<stream> rather than via SetStreamMetadataAsync, which
        // generates a new event id per call: reusing the source event's id lets KurrentDB deduplicate a retry whose
        // first attempt landed but whose response was lost.
        async Task<long> SetStreamMeta(ProposedMetaEvent meta) {
            if (Log.IsDebugEnabled())
                Log.Debug("Setting meta for {Stream} to {Meta}", meta.EventDetails.Stream, meta);

            var data = new EventData(
                Uuid.FromGuid(meta.EventDetails.EventId),
                SystemEventTypes.StreamMetadata,
                SerializeMetadata(meta.Data),
                contentType: "application/json"
            );

            var result = await Write(
                    (a, c) => client.AppendToStreamAsync(
                        SystemStreams.MetastreamOf(meta.EventDetails.Stream),
                        StreamState.Any,
                        [data],
                        userCredentials: a.Credentials,
                        cancellationToken: c
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);

            return (long)result.LogPosition.CommitPosition;
        }
    }

    /// <summary>
    /// Runs one write, retrying transient failures (sink node unreachable, restarting, overloaded) with backoff until
    /// it succeeds, fails with a non-transient error, or the wait is cancelled by shutdown or the caller. The gRPC
    /// call itself only gets the caller's token, so shutdown never cancels a write in flight.
    /// </summary>
    async Task<T> Write<T>(Func<CallAuth, CancellationToken, Task<T>> call, CancellationToken ct) {
        var attempt = 0;

        while (true) {
            try {
                var result = await auth.Run(call, ct).ConfigureAwait(false);

                if (attempt > 0 && Interlocked.Exchange(ref _outage, 0) == 1) {
                    Log.Info("{Side}: KurrentDB writes succeed again", auth.Side);
                    _warn.Reset();
                }

                return result;
            } catch (Exception e) when (TransientFailure.IsTransient(e)) {
                Volatile.Write(ref _outage, 1);

                if (_warn.ShouldLog())
                    Log.Warn("{Side}: KurrentDB write failed ({Error}); retrying with backoff until it succeeds", auth.Side, TransientFailure.Describe(e));

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, auth.Shutdown);
                await Task.Delay(TokenGate.Backoff(attempt++), auth.Time, linked.Token).ConfigureAwait(false);
            }
        }
    }

    static EventData Map(ProposedEvent evt)
        => new(
            Uuid.FromGuid(evt.EventDetails.EventId),
            evt.EventDetails.EventType,
            evt.Data,
            evt.Metadata,
            evt.EventDetails.ContentType
        );

    /// <summary>
    /// The <c>$metadata</c> event body in KurrentDB's system metadata format, using the same converter the readers
    /// use to parse it. The contract carries no custom metadata (the readers drop it), so none is written.
    /// </summary>
    internal static byte[] SerializeMetadata(Shared.Contracts.StreamMetadata meta)
        => JsonSerializer.SerializeToUtf8Bytes(
            new StreamMetadata(
                meta.MaxCount,
                meta.MaxAge,
                meta.TruncateBefore is { } tb ? new StreamPosition((ulong)tb) : null,
                meta.CacheControl,
                meta.StreamAcl is { } acl
                    ? new StreamAcl(acl.ReadRoles, acl.WriteRoles, acl.DeleteRoles, acl.MetaReadRoles, acl.MetaWriteRoles)
                    : null
            ),
            MetaSerialization.StreamMetadataJsonSerializerOptions
        );
}
