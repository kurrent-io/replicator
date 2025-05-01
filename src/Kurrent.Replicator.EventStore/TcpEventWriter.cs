using Kurrent.Replicator.Shared.Contracts;
using Kurrent.Replicator.Shared.Logging;
using Kurrent.Replicator.Shared.Observe;
using Ubiquitous.Metrics;
using StreamMetadata = EventStore.ClientAPI.StreamMetadata;

// ReSharper disable SuggestBaseTypeForParameter
namespace Kurrent.Replicator.EventStore;

public class TcpEventWriter(IEventStoreConnection connection) : IEventWriter {
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();

    public Task Start() => connection.ConnectAsync();

    public Task<long> WriteEvent(BaseProposedEvent proposedEvent, CancellationToken cancellationToken) {
        var task = proposedEvent switch {
            ProposedEvent p             => Append(p),
            ProposedMetaEvent meta      => SetMeta(meta),
            ProposedDeleteStream delete => Delete(delete),
            IgnoredEvent _              => Task.FromResult(-1L),
            _                           => throw new InvalidOperationException("Unknown proposed event type")
        };

        return Metrics.Measure(() => task, ReplicationMetrics.WritesHistogram, ReplicationMetrics.WriteErrorsCount);

        async Task<long> Append(ProposedEvent p) {
            if (Log.IsDebugEnabled()) {
                Log.Debug(
                    "TCP: Write event with id {Id} of type {Type} to {Stream} with original position {Position}",
                    proposedEvent.EventDetails.EventId,
                    proposedEvent.EventDetails.EventType,
                    proposedEvent.EventDetails.Stream,
                    proposedEvent.SourceLogPosition.EventPosition
                );
            }

            var result = await connection.AppendToStreamAsync(p.EventDetails.Stream, ExpectedVersion.Any, Map(p)).ConfigureAwait(false);

            return result.LogPosition.CommitPosition;
        }

        async Task<long> SetMeta(ProposedMetaEvent meta) {
            if (Log.IsDebugEnabled())
                Log.Debug(
                    "TCP: Setting metadata to {Stream} with original position {Position}",
                    proposedEvent.EventDetails.Stream,
                    proposedEvent.SourceLogPosition.EventPosition
                );

            var result = await connection.SetStreamMetadataAsync(
                    meta.EventDetails.Stream,
                    ExpectedVersion.Any,
                    StreamMetadata.Create(
                        meta.Data.MaxCount,
                        meta.Data.MaxAge,
                        meta.Data.TruncateBefore,
                        meta.Data.CacheControl,
                        new(
                            meta.Data.StreamAcl?.ReadRoles,
                            meta.Data.StreamAcl?.WriteRoles,
                            meta.Data.StreamAcl?.DeleteRoles,
                            meta.Data.StreamAcl?.MetaReadRoles,
                            meta.Data.StreamAcl?.MetaWriteRoles
                        )
                    )
                )
                .ConfigureAwait(false);

            return result.LogPosition.CommitPosition;
        }

        async Task<long> Delete(ProposedDeleteStream delete) {
            if (Log.IsDebugEnabled()) {
                Log.Debug(
                    "TCP: Deleting stream {Stream} with original position {Position}",
                    proposedEvent.EventDetails.Stream,
                    proposedEvent.SourceLogPosition.EventPosition
                );
            }

            var result = await connection.DeleteStreamAsync(delete.EventDetails.Stream, ExpectedVersion.Any).ConfigureAwait(false);

            return result.LogPosition.CommitPosition;
        }

        static EventData Map(ProposedEvent evt) => new(
            evt.EventDetails.EventId,
            evt.EventDetails.EventType,
            evt.EventDetails.ContentType == ContentTypes.Json,
            evt.Data,
            evt.Metadata
        );
    }
}
