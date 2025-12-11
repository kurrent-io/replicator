// ReSharper disable SuggestBaseTypeForParameter

namespace Kurrent.Replicator.Shared.Contracts;

public abstract record BaseProposedEvent(EventDetails EventDetails, LogPosition SourceLogPosition, long SequenceNumber, Guid ReplicationMessageId) {
    /// <summary>
    /// Indicates whether this proposed event originated from a metadata/system event (e.g. $>, $@)
    /// rather than a domain event. Computed from the <see cref="EventDetails.EventType"/> so that
    /// existing record constructors do not need to change.
    /// </summary>
    public bool IsMetadata => EventDetails.EventType.StartsWith("$");
}

public record ProposedEvent(
        EventDetails EventDetails,
        byte[]       Data,
        byte[]?      Metadata,
        LogPosition  SourceLogPosition,
        long         SequenceNumber,
        Guid         ReplicationMessageId
    ) : BaseProposedEvent(EventDetails, SourceLogPosition, SequenceNumber, ReplicationMessageId);

public record ProposedMetaEvent(
        EventDetails   EventDetails,
        StreamMetadata Data,
        LogPosition    SourceLogPosition,
        long           SequenceNumber,
        Guid           ReplicationMessageId
    ) : BaseProposedEvent(EventDetails, SourceLogPosition, SequenceNumber, ReplicationMessageId);

public record ProposedDeleteStream(EventDetails EventDetails, LogPosition SourceLogPosition, long SequenceNumber, Guid ReplicationMessageId)
    : BaseProposedEvent(EventDetails, SourceLogPosition, SequenceNumber, ReplicationMessageId);

public record IgnoredEvent(EventDetails EventDetails, LogPosition SourceLogPosition, long SequenceNumber, Guid ReplicationMessageId)
    : BaseProposedEvent(EventDetails, SourceLogPosition, SequenceNumber, ReplicationMessageId);

public record NoEvent(EventDetails EventDetails, LogPosition SourceLogPosition, long SequenceNumber, Guid ReplicationMessageId)
    : BaseProposedEvent(EventDetails, SourceLogPosition, SequenceNumber, ReplicationMessageId);
