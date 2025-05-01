using Kurrent.Replicator.Shared.Observe;
using Ubiquitous.Metrics;

namespace Kurrent.Replicator.EventStore;

static class ConnectionExtensions {
    public static async Task<StreamSize> GetStreamSize(this IEventStoreConnection connection, string stream) {
        var last = await connection.ReadStreamEventsBackwardAsync(stream, StreamPosition.End, 1, false).ConfigureAwait(false);

        return new(last.LastEventNumber);
    }

    public static async Task<StreamMeta> GetStreamMeta(this IEventStoreConnection connection, string stream) {
        var streamMeta = await Metrics.Measure(() => connection.GetStreamMetadataAsync(stream), ReplicationMetrics.MetaReadsHistogram).ConfigureAwait(false);

        return new(
            streamMeta.IsStreamDeleted,
            default,
            streamMeta.StreamMetadata.MaxAge,
            streamMeta.StreamMetadata.MaxCount,
            streamMeta.StreamMetadata.TruncateBefore,
            streamMeta.MetastreamVersion
        );
    }
}
