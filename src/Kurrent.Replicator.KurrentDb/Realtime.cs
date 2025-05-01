using System.Text.Json;
using Kurrent.Replicator.KurrentDb.Internals;

namespace Kurrent.Replicator.KurrentDb;

class Realtime(EventStoreClient client, StreamMetaCache metaCache) {
    bool _started;

    public Task Start() {
        if (_started) return Task.CompletedTask;

        _started = true;

        return client.SubscribeToAllAsync(FromAll.End, (_, evt, _) => HandleEvent(evt), subscriptionDropped: HandleDrop);
    }

    void HandleDrop(StreamSubscription subscription, SubscriptionDroppedReason reason, Exception? exception) {
        if (reason == SubscriptionDroppedReason.Disposed) return;

        _started = false;
        Task.Run(Start);
    }

    Task HandleEvent(ResolvedEvent re) {
        if (IsSystemEvent())
            return Task.CompletedTask;

        if (IsMetadataUpdate()) {
            var stream = re.OriginalStreamId[2..];
            var meta   = JsonSerializer.Deserialize<StreamMetadata>(re.Event.Data.Span, MetaSerialization.StreamMetadataJsonSerializerOptions);
            metaCache.UpdateStreamMeta(stream, meta, re.OriginalEventNumber.ToInt64());
        }
        else {
            metaCache.UpdateStreamLastEventNumber(re.OriginalStreamId, re.OriginalEventNumber.ToInt64());
        }

        return Task.CompletedTask;

        bool IsSystemEvent() => re.Event.EventType.StartsWith('$') && re.Event.EventType != Predefined.MetadataEventType;

        bool IsMetadataUpdate() => re.Event.EventType == Predefined.MetadataEventType;
    }
}
