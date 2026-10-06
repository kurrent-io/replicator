using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Shared.Contracts;

namespace Kurrent.Replicator.KurrentDb;

class ScavengedEventsFilter(
        Func<string, CallAuth, CancellationToken, Task<StreamMeta>> readMeta,
        Func<string, CallAuth, CancellationToken, Task<StreamSize>> readSize,
        StreamMetaCache                                             cache,
        GrpcAuthContext                                             auth
    ) {
    public ScavengedEventsFilter(EventStoreClient client, StreamMetaCache cache, GrpcAuthContext auth)
        : this(MetaReader(client), SizeReader(client), cache, auth) { }

    internal static Func<string, CallAuth, CancellationToken, Task<StreamMeta>> MetaReader(EventStoreClient client) =>
        (s, a, c) => client.GetStreamMeta(s, a.Credentials, c);

    internal static Func<string, CallAuth, CancellationToken, Task<StreamSize>> SizeReader(EventStoreClient client) =>
        (s, a, c) => client.GetStreamSize(s, a.Credentials, c);

    public async ValueTask<bool> Filter(BaseOriginalEvent originalEvent) {
        var stream = originalEvent.EventDetails.Stream;

        var meta = await cache.GetOrAddStreamMeta(stream, s => auth.Run((a, c) => readMeta(s, a, c), auth.Shutdown)).ConfigureAwait(false);

        return meta == null || !meta.IsDeleted && !TtlExpired() && !await OverMaxCount().ConfigureAwait(false);

        bool TtlExpired() => meta.MaxAge.HasValue && originalEvent.Created < DateTime.Now - meta.MaxAge;

        // add the check timestamp, so we can check again if we get newer events (edge case)
        async Task<bool> OverMaxCount() {
            if (!meta.MaxCount.HasValue)
                return false;

            var streamSize = await cache.GetOrAddStreamSize(stream, s => auth.Run((a, c) => readSize(s, a, c), auth.Shutdown)).ConfigureAwait(false);

            return originalEvent.LogPosition.EventNumber < streamSize.LastEventNumber - meta.MaxCount;
        }
    }
}
