using Kurrent.Replicator.Shared.Contracts;

namespace Kurrent.Replicator.KurrentDb;

class ScavengedEventsFilter(EventStoreClient client, StreamMetaCache cache) {
    public async ValueTask<bool> Filter(BaseOriginalEvent originalEvent) {
        var meta = await cache.GetOrAddStreamMeta(originalEvent.EventDetails.Stream, client.GetStreamMeta).ConfigureAwait(false);

        return meta == null || !meta.IsDeleted && !TtlExpired() && !await OverMaxCount().ConfigureAwait(false);

        bool TtlExpired() => meta.MaxAge.HasValue && originalEvent.Created < DateTime.Now - meta.MaxAge;

        // add the check timestamp, so we can check again if we get newer events (edge case)
        async Task<bool> OverMaxCount() {
            if (!meta.MaxCount.HasValue)
                return false;

            var streamSize = await cache.GetOrAddStreamSize(originalEvent.EventDetails.Stream, client.GetStreamSize).ConfigureAwait(false);

            return originalEvent.LogPosition.EventNumber < streamSize.LastEventNumber - meta.MaxCount;
        }
    }
}
