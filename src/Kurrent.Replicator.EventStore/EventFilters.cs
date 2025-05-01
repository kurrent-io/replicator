using Kurrent.Replicator.Shared.Contracts;

namespace Kurrent.Replicator.EventStore;

class ScavengedEventsFilter(IEventStoreConnection connection, StreamMetaCache cache) {
    public async ValueTask<bool> Filter(BaseOriginalEvent originalEvent) {
        if (originalEvent is not OriginalEvent) return true;

        var meta      = (await cache.GetOrAddStreamMeta(originalEvent.EventDetails.Stream, connection.GetStreamMeta).ConfigureAwait(false))!;
        var isDeleted = meta.IsDeleted && meta.DeletedAt > originalEvent.Created;

        return !isDeleted && !Truncated() && !TtlExpired() && !await OverMaxCount().ConfigureAwait(false);

        bool TtlExpired() => meta.MaxAge.HasValue && originalEvent.Created < DateTime.Now - meta.MaxAge;

        bool Truncated() => meta.TruncateBefore.HasValue && originalEvent.LogPosition.EventNumber < meta.TruncateBefore;

        // add the check timestamp, so we can check again if we get newer events (edge case)
        async Task<bool> OverMaxCount() {
            if (!meta.MaxCount.HasValue) return false;

            var streamSize = await cache.GetOrAddStreamSize(originalEvent.EventDetails.Stream, connection.GetStreamSize).ConfigureAwait(false);

            return originalEvent.LogPosition.EventNumber < streamSize.LastEventNumber - meta.MaxCount;
        }
    }
}
