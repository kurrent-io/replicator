using System.Text.Json.Serialization;

namespace Kurrent.Replicator.Shared.Contracts; 

public record EventMetadata {
    public const string EventNumberPropertyName = "$originalEventNumber";
    public const string PositionPropertyName    = "$originalPosition";
    public const string CreatedDate             = "$originalCreatedDate";
        
    [JsonPropertyName(EventNumberPropertyName)]
    public long OriginalEventNumber { get; init; }

    [JsonPropertyName(PositionPropertyName)]
    public ulong OriginalPosition { get; init; }

    [JsonPropertyName(CreatedDate)]
    public DateTimeOffset OriginalCreatedDate { get; init; }
}