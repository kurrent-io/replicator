using Kurrent.Replicator.Shared.Contracts;
using Kurrent.Replicator.Shared.Logging;
using Kurrent.Replicator.Shared.Observe;
using Ubiquitous.Metrics;
using StreamAcl = EventStore.Client.StreamAcl;

namespace Kurrent.Replicator.KurrentDb;

public class GrpcEventWriter(EventStoreClient client) : IEventWriter {
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();

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

            var result = await client.AppendToStreamAsync(proposedEvent.EventDetails.Stream, StreamState.Any, [Map(p)], cancellationToken: cancellationToken).ConfigureAwait(false);

            return (long)result.LogPosition.CommitPosition;
        }

        async Task<long> DeleteStream(string stream) {
            if (Log.IsDebugEnabled())
                Log.Debug("Deleting stream {Stream}", stream);

            var result = await client.DeleteAsync(stream, StreamState.Any, cancellationToken: cancellationToken).ConfigureAwait(false);

            return (long)result.LogPosition.CommitPosition;
        }

        async Task<long> SetStreamMeta(ProposedMetaEvent meta) {
            if (Log.IsDebugEnabled())
                Log.Debug("Setting meta for {Stream} to {Meta}", meta.EventDetails.Stream, meta);

            var result = await client.SetStreamMetadataAsync(
                    meta.EventDetails.Stream,
                    StreamState.Any,
                    new(
                        meta.Data.MaxCount,
                        meta.Data.MaxAge,
                        ValueOrNull(meta.Data.TruncateBefore, x => new StreamPosition((ulong)x!)),
                        meta.Data.CacheControl,
                        ValueOrNull(
                            meta.Data.StreamAcl,
                            x =>
                                new StreamAcl(
                                    x.ReadRoles,
                                    x.WriteRoles,
                                    x.DeleteRoles,
                                    x.MetaReadRoles,
                                    x.MetaWriteRoles
                                )
                        )
                    ),
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false);

            return (long)result.LogPosition.CommitPosition;
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

    static T? ValueOrNull<T1, T>(T1? source, Func<T1, T> transform)
        => source == null ? default : transform(source);
}
