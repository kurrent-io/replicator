// ReSharper disable SuggestBaseTypeForParameter

namespace Kurrent.Replicator.Shared.Contracts;

public abstract record BaseOriginalEvent(
        DateTimeOffset  Created,
        EventDetails    EventDetails,
        LogPosition     LogPosition,
        long            SequenceNumber,
        TracingMetadata TracingMetadata
    );

public record OriginalEvent(
        DateTimeOffset  Created,
        EventDetails    EventDetails,
        byte[]          Data,
        byte[]?         Metadata,
        LogPosition     LogPosition,
        long            SequenceNumber,
        TracingMetadata TracingMetadata
    ) : BaseOriginalEvent(Created, EventDetails, LogPosition, SequenceNumber, TracingMetadata);

public record StreamMetadataOriginalEvent(
        DateTimeOffset  Created,
        EventDetails    EventDetails,
        StreamMetadata  Data,
        LogPosition     LogPosition,
        long            SequenceNumber,
        TracingMetadata TracingMetadata
    ) : BaseOriginalEvent(Created, EventDetails, LogPosition, SequenceNumber, TracingMetadata);

public record StreamDeletedOriginalEvent(
        DateTimeOffset  Created,
        EventDetails    EventDetails,
        LogPosition     LogPosition,
        long            SequenceNumber,
        TracingMetadata TracingMetadata
    ) : BaseOriginalEvent(Created, EventDetails, LogPosition, SequenceNumber, TracingMetadata);

public record IgnoredOriginalEvent(
        DateTimeOffset  Created,
        EventDetails    EventDetails,
        LogPosition     LogPosition,
        long            SequenceNumber,
        TracingMetadata TracingMetadata
    )
    : BaseOriginalEvent(Created, EventDetails, LogPosition, SequenceNumber, TracingMetadata);
