// ReSharper disable SuggestBaseTypeForParameter

namespace Kurrent.Replicator.Shared.Contracts;

public abstract record BaseOriginalEvent(
        DateTimeOffset  Created,
        EventDetails    EventDetails,
        LogPosition     LogPosition,
        long            SequenceNumber,
        TracingMetadata TracingMetadata,
        Guid            ReplicationMessageId
    ) {
    /// <summary>
    /// Indicates whether this event is a metadata/system event (e.g. $>, $@) as opposed to a domain event.
    /// Computed on demand from the <see cref="EventDetails.EventType"/> so that existing record constructors
    /// do not need to change.
    /// </summary>
    public bool IsMetadata => EventDetails.EventType.StartsWith("$");
}

public record OriginalEvent(
        DateTimeOffset  Created,
        EventDetails    EventDetails,
        byte[]          Data,
        byte[]?         Metadata,
        LogPosition     LogPosition,
        long            SequenceNumber,
        TracingMetadata TracingMetadata,
        Guid            ReplicationMessageId
    ) : BaseOriginalEvent(Created, EventDetails, LogPosition, SequenceNumber, TracingMetadata, ReplicationMessageId);

public record StreamMetadataOriginalEvent(
        DateTimeOffset  Created,
        EventDetails    EventDetails,
        StreamMetadata  Data,
        LogPosition     LogPosition,
        long            SequenceNumber,
        TracingMetadata TracingMetadata,
        Guid            ReplicationMessageId
    ) : BaseOriginalEvent(Created, EventDetails, LogPosition, SequenceNumber, TracingMetadata, ReplicationMessageId);

public record StreamDeletedOriginalEvent(
        DateTimeOffset  Created,
        EventDetails    EventDetails,
        LogPosition     LogPosition,
        long            SequenceNumber,
        TracingMetadata TracingMetadata,
        Guid            ReplicationMessageId
    ) : BaseOriginalEvent(Created, EventDetails, LogPosition, SequenceNumber, TracingMetadata, ReplicationMessageId);

public record IgnoredOriginalEvent(
        DateTimeOffset  Created,
        EventDetails    EventDetails,
        LogPosition     LogPosition,
        long            SequenceNumber,
        TracingMetadata TracingMetadata,
        Guid            ReplicationMessageId
    )
    : BaseOriginalEvent(Created, EventDetails, LogPosition, SequenceNumber, TracingMetadata, ReplicationMessageId);
