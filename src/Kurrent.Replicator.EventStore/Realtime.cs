using Kurrent.Replicator.Shared.Logging;

namespace Kurrent.Replicator.EventStore;

class Realtime(IEventStoreConnection connection, StreamMetaCache metaCache) {
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();

    bool _started;

    public Task Start() {
        if (_started) return Task.CompletedTask;

        Log.Info("Starting realtime subscription for meta updates");
        _started = true;

        return connection.SubscribeToAllAsync(false, (_, re) => HandleEvent(re), HandleDrop);
    }

    void HandleDrop(EventStoreSubscription subscription, SubscriptionDropReason reason, Exception exception) {
        Log.Info("Connection dropped because {Reason}", reason);

        if (reason is SubscriptionDropReason.UserInitiated) return;

        _started = false;

        var task = reason is SubscriptionDropReason.ConnectionClosed ? StartAfterDelay() : Start();
        Task.Run(() => task);

        return;

        async Task StartAfterDelay() {
            await Task.Delay(5000).ConfigureAwait(false);
            await Start().ConfigureAwait(false);
        }
    }

    Task HandleEvent(ResolvedEvent re) {
        if (IsSystemEvent()) {
            return Task.CompletedTask;
        }

        if (IsMetadataUpdate()) {
            var stream = re.OriginalStreamId[2..];
            var meta   = StreamMetadata.FromJsonBytes(re.Event.Data);

            if (Log.IsDebugEnabled()) {
                Log.Debug("Real-time meta update {Stream}: {Meta}", stream, meta);
            }

            metaCache.UpdateStreamMeta(stream, meta, re.OriginalEventNumber, re.OriginalEvent.Created);
        }
        else {
            metaCache.UpdateStreamLastEventNumber(re.OriginalStreamId, re.OriginalEventNumber);
        }

        return Task.CompletedTask;

        bool IsSystemEvent() => re.Event.EventType.StartsWith('$') && re.Event.EventType != Predefined.MetadataEventType;

        bool IsMetadataUpdate() => re.Event.EventType == Predefined.MetadataEventType;
    }
}
