using Kurrent.Replicator.Js;
using Kurrent.Replicator.Shared.Contracts;
using Jint;
using Jint.Native;
using Jint.Native.Json;

// ReSharper disable NotAccessedPositionalProperty.Global

namespace Kurrent.Replicator.Kafka;

public class KafkaJsMessageRouter(string routingFunction) {
    readonly TypedJsFunction<RouteEvent, MessageRoute> _function = new(routingFunction, "route", AsRoute);

    static MessageRoute AsRoute(JsValue? result, RouteEvent evt) {
        if (result == null || result.IsUndefined() || !result.IsObject())
            return DefaultRouters.RouteByCategory(evt.Stream);

        var obj = result.AsObject();

        return new(obj.Get("topic").AsString(), obj.Get("partitionKey").AsString());
    }

    public MessageRoute Route(ProposedEvent evt) {
        var parser = new JsonParser(_function.Engine);

        return _function.Execute(
            new(
                evt.EventDetails.Stream,
                evt.EventDetails.EventType,
                parser.Parse(evt.Data.AsUtf8String()),
                evt.Metadata == null ? null : parser.Parse(evt.Metadata.AsUtf8String())
            )
        );
    }
}

record RouteEvent(string Stream, string EventType, object Data, object? Meta);

public record MessageRoute(string Topic, string PartitionKey);
