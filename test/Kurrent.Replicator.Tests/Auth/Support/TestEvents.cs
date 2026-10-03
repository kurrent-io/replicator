#nullable enable
using System.Diagnostics;
using Kurrent.Replicator.Shared;
using Kurrent.Replicator.Shared.Contracts;

namespace Kurrent.Replicator.Tests.Auth.Support;

public static class TestEvents {
    static EventDetails Details(string stream) => new(stream, Guid.NewGuid(), "TestEvent", ContentTypes.Json);

    static readonly TracingMetadata Tracing = new(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom());

    public static ProposedEvent Proposed(string stream, long eventNumber = 0)
        => new(Details(stream), "{}"u8.ToArray(), null, new LogPosition(eventNumber, (ulong)eventNumber + 1), eventNumber);

    public static ProposedMetaEvent Meta(string stream)
        => new(Details(stream), new StreamMetadata(10, null, null, null, null), new LogPosition(0, 1), 0);

    public static ProposedDeleteStream Delete(string stream) => new(Details(stream), new LogPosition(0, 1), 0);

    public static OriginalEvent Original(string stream, long eventNumber, DateTimeOffset? created = null)
        => new(created ?? DateTimeOffset.UtcNow, Details(stream), "{}"u8.ToArray(), null, new LogPosition(eventNumber, (ulong)eventNumber + 1), eventNumber, Tracing);
}
