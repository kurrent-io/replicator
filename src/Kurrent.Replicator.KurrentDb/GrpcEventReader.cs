using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kurrent.Replicator.KurrentDb.Internals;
using Kurrent.Replicator.Shared.Contracts;
using Kurrent.Replicator.Shared.Logging;
using Kurrent.Replicator.Shared.Observe;
using Ubiquitous.Metrics;
using Position = EventStore.Client.Position;
using StreamMetadata = EventStore.Client.StreamMetadata;

namespace Kurrent.Replicator.KurrentDb;

public class GrpcEventReader : IEventReader {
    public string Protocol => "grpc";

    readonly ILog  _log;
    readonly ILog? _debugLog;

    const string StreamDeletedBody = "{\"$tb\":9223372036854775807}";

    readonly EventStoreClient      _client;
    readonly ScavengedEventsFilter _filter;
    readonly Realtime              _realtime;

    public GrpcEventReader(EventStoreClient client) {
        _log      = LogProvider.GetCurrentClassLogger();
        _debugLog = _log.IsDebugEnabled() ? _log : null;

        var metaCache = new StreamMetaCache();
        _client   = client;
        _filter   = new(client, metaCache);
        _realtime = new(client, metaCache);
    }

    public async Task ReadEvents(LogPosition fromLogPosition, Func<BaseOriginalEvent, ValueTask> next, CancellationToken cancellationToken) {
        var sequence     = 0;
        var lastPosition = 0L;

        _log.Info("Starting gRPC reader");

        await _realtime.Start();

        var (_, eventPosition) = fromLogPosition;

        var read = _client.ReadAllAsync(Direction.Forwards, new(eventPosition, eventPosition), cancellationToken: cancellationToken);

        var enumerator = read.GetAsyncEnumerator(cancellationToken);

        do {
            using var activity = new Activity("read");
            activity.Start();

            var hasValue = await Metrics.MeasureValueTask(
                    () => enumerator.MoveNextAsync(cancellationToken),
                    ReplicationMetrics.ReadsHistogram,
                    ReplicationMetrics.ReadErrorsCount
                )
                .ConfigureAwait(false);

            if (!hasValue) break;

            var evt = enumerator.Current;
            lastPosition = (long)(evt.OriginalPosition?.CommitPosition ?? 0);
            var replicationMessageId = Guid.NewGuid();

            var eventType  = evt.Event.EventType;
            var isMetadata = eventType.StartsWith("$");

            _debugLog?.Debug(
                "gRPC: Read event with id {Id} of type {Type} from {Stream} at {Position}, ReplicationMessageId={ReplicationMessageId}",
                evt.Event.EventId,
                eventType,
                evt.OriginalStreamId,
                evt.OriginalPosition,
                replicationMessageId
            );

            if (ReplicationDebugOptions.DebugPartitionSequences) {
                _debugLog?.Debug(
                    "Reader sequence. Position={Position}, EventType={EventType}, EventId={EventId}, Stream={Stream}, ReplicationMessageId={ReplicationMessageId}",
                    evt.OriginalPosition,
                    eventType,
                    evt.Event.EventId,
                    evt.OriginalStreamId,
                    replicationMessageId
                );

                _debugLog?.Debug(
                    "Event classification. Type={EventType}, IsMetadata={IsMetadata}, Stream={Stream}, Position={Position}, ReplicationMessageId={ReplicationMessageId}",
                    eventType,
                    isMetadata,
                    evt.OriginalStreamId,
                    evt.OriginalPosition,
                    replicationMessageId
                );
            }

            BaseOriginalEvent originalEvent;

            if (evt.Event.EventType == Predefined.MetadataEventType) {
                if (Encoding.UTF8.GetString(evt.Event.Data.Span) == StreamDeletedBody) {
                    originalEvent = MapStreamDeleted(evt, sequence++, activity, replicationMessageId);
                }
                else {
                    originalEvent = MapMetadata(evt, sequence++, activity, replicationMessageId);
                }
            }
            else if (evt.Event.EventType[0] != '$') {
                originalEvent = Map(evt, sequence++, activity, replicationMessageId);
            }
            else {
                await next(MapIgnored(evt, sequence++, activity, replicationMessageId)).ConfigureAwait(false);

                continue;
            }

            await next(originalEvent).ConfigureAwait(false);
        } while (true);

        _log.Info("Reached the end of the stream at {Position}", lastPosition);
    }

    public async Task<long?> GetLastPosition(CancellationToken cancellationToken) {
        var events = await _client
            .ReadAllAsync(Direction.Backwards, Position.End, 1, cancellationToken: cancellationToken)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        return (long?)events[0].OriginalPosition?.CommitPosition;
    }

    static IgnoredOriginalEvent MapIgnored(ResolvedEvent evt, int sequence, Activity activity, Guid replicationMessageId)
        => new(
            evt.Event.Created,
            MapDetails(evt.Event),
            MapPosition(evt),
            sequence,
            new(activity.TraceId, activity.SpanId),
            replicationMessageId
        );

    static OriginalEvent Map(ResolvedEvent evt, int sequence, Activity activity, Guid replicationMessageId)
        => new(
            evt.OriginalEvent.Created,
            MapDetails(evt.OriginalEvent),
            evt.OriginalEvent.Data.ToArray(),
            evt.OriginalEvent.Metadata.ToArray(),
            MapPosition(evt),
            sequence,
            new(activity.TraceId, activity.SpanId),
            replicationMessageId
        );

    static StreamMetadataOriginalEvent MapMetadata(ResolvedEvent evt, int sequence, Activity activity, Guid replicationMessageId) {
        var streamMeta = JsonSerializer.Deserialize<StreamMetadata>(
            evt.Event.Data.Span,
            MetaSerialization.StreamMetadataJsonSerializerOptions
        );

        return new(
            evt.OriginalEvent.Created,
            MapSystemDetails(evt.OriginalEvent),
            new(
                streamMeta.MaxCount,
                streamMeta.MaxAge,
                streamMeta.TruncateBefore?.ToInt64(),
                streamMeta.CacheControl,
                new(
                    streamMeta.Acl?.ReadRoles,
                    streamMeta.Acl?.WriteRoles,
                    streamMeta.Acl?.DeleteRoles,
                    streamMeta.Acl?.MetaReadRoles,
                    streamMeta.Acl?.MetaWriteRoles
                )
            ),
            MapPosition(evt),
            sequence,
            new(activity.TraceId, activity.SpanId),
            replicationMessageId
        );
    }

    static StreamDeletedOriginalEvent MapStreamDeleted(ResolvedEvent evt, int sequence, Activity activity, Guid replicationMessageId)
        => new(
            evt.OriginalEvent.Created,
            MapSystemDetails(evt.OriginalEvent),
            MapPosition(evt),
            sequence,
            new(activity.TraceId, activity.SpanId),
            replicationMessageId
        );

    static EventDetails MapDetails(EventRecord evt) =>
        new(evt.EventStreamId, evt.EventId.ToGuid(), evt.EventType, evt.ContentType);

    static EventDetails MapSystemDetails(EventRecord evt) =>
        new(evt.EventStreamId[2..], evt.EventId.ToGuid(), evt.EventType, "");

    static LogPosition MapPosition(ResolvedEvent evt) =>
        new(evt.OriginalEventNumber.ToInt64(), evt.OriginalPosition!.Value.CommitPosition);

    public ValueTask<bool> Filter(BaseOriginalEvent originalEvent) => _filter.Filter(originalEvent);
}
