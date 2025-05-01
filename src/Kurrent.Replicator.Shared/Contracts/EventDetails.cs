namespace Kurrent.Replicator.Shared.Contracts;

public record EventDetails(string Stream, Guid EventId, string EventType, string ContentType);

public static class ContentTypes {
    public const string Json   = "application/json";
    public const string Binary = "application/octet-stream";
}
