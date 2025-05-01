// ReSharper disable SuggestBaseTypeForParameter

namespace Kurrent.Replicator.Shared.Contracts;

public abstract record BaseProposedEvent(EventDetails EventDetails, LogPosition SourceLogPosition, long SequenceNumber);

public record ProposedEvent(
        EventDetails EventDetails,
        byte[]       Data,
        byte[]?      Metadata,
        LogPosition  SourceLogPosition,
        long         SequenceNumber
    ) : BaseProposedEvent(EventDetails, SourceLogPosition, SequenceNumber);

public record ProposedMetaEvent(
        EventDetails   EventDetails,
        StreamMetadata Data,
        LogPosition    SourceLogPosition,
        long           SequenceNumber
    ) : BaseProposedEvent(EventDetails, SourceLogPosition, SequenceNumber);

public record ProposedDeleteStream(EventDetails EventDetails, LogPosition SourceLogPosition, long SequenceNumber)
    : BaseProposedEvent(EventDetails, SourceLogPosition, SequenceNumber);

public record IgnoredEvent(EventDetails EventDetails, LogPosition SourceLogPosition, long SequenceNumber)
    : BaseProposedEvent(EventDetails, SourceLogPosition, SequenceNumber);

public record NoEvent(EventDetails EventDetails, LogPosition SourceLogPosition, long SequenceNumber)
    : BaseProposedEvent(EventDetails, SourceLogPosition, SequenceNumber);
