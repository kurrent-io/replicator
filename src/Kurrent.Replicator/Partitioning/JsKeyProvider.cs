using Kurrent.Replicator.Shared.Contracts;
using Jint;
using Jint.Native;
using Jint.Native.Json;
using Kurrent.Replicator.Js;

namespace Kurrent.Replicator.Partitioning;

public class JsKeyProvider(string partitionFunction) {
    readonly TypedJsFunction<PartitionEvent, string?> _function = new(partitionFunction, "partition", AsPartition);

    static string? AsPartition(JsValue? result, PartitionEvent evt)
        => result == null || result.IsUndefined() || !result.IsString() ? null : result.ToString();

    public string GetPartitionKey(BaseProposedEvent original) {
        if (original is not ProposedEvent evt) {
            return Default();
        }

        var parser = new JsonParser(_function.Engine);

        var result = _function.Execute(
            new PartitionEvent(
                original.EventDetails.Stream,
                original.EventDetails.EventType,
                parser.Parse(evt.Data.AsUtf8String()),
                evt.Metadata != null ? parser.Parse(evt.Metadata.AsUtf8String()) : null
            )
        );

        return result ?? Default();

        string Default() => KeyProvider.ByStreamName(original);
    }
}

record PartitionEvent(string Stream, string EventType, object Data, object? Meta);
