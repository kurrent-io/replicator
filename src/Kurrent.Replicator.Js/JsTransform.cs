using Esprima;
using Kurrent.Replicator.Shared.Contracts;
using Kurrent.Replicator.Shared.Logging;
using Jint;
using Jint.Native;
using Jint.Native.Json;
using JsonSerializer = System.Text.Json.JsonSerializer;

// ReSharper disable NotAccessedPositionalProperty.Local

namespace Kurrent.Replicator.Js;

public class JsTransform(string jsFunc) {
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();

    readonly TypedJsFunction<TransformEvent, TransformedEvent?> _function = new(jsFunc, "transform", HandleResponse);

    static TransformedEvent? HandleResponse(JsValue? result, TransformEvent original) {
        if (result == null || result.IsUndefined()) {
            Log.Debug("Got empty response, ignoring");

            return null;
        }

        var obj = result.AsObject();

        if (!TryGetString("Stream", true, out var stream)       ||
            string.IsNullOrWhiteSpace(stream)                   ||
            !TryGetString("EventType", true, out var eventType) ||
            string.IsNullOrWhiteSpace(eventType))
            return null;

        var data = GetSerializedObject("Data");

        if (data == null) return null;

        var meta = GetSerializedObject("Meta");

        return new(stream, eventType, data, meta);

        byte[]? GetSerializedObject(string propName) {
            var candidate = obj.Get(propName);

            if (candidate == JsValue.Undefined || !candidate.IsObject()) {
                return null;
            }

            return JsonSerializer.SerializeToUtf8Bytes(candidate.ToObject());
        }

        bool TryGetString(string propName, bool log, out string value) {
            var candidate = obj.Get(propName);

            if (candidate == JsValue.Undefined || !candidate.IsString()) {
                if (log) Log.Debug("Transformed object property {Prop} is null or not a string", propName);
                value = string.Empty;

                return false;
            }

            value = candidate.AsString();

            return true;
        }
    }

    static readonly ParserOptions ParserOptions = new() { Tolerant = true };

    public ValueTask<BaseProposedEvent> Transform(OriginalEvent original, CancellationToken cancellationToken) {
        var parser = new JsonParser(_function.Engine);

        var result = _function.Execute(
            new(
                original.Created,
                original.EventDetails.Stream,
                original.EventDetails.EventType,
                parser.Parse(original.Data.AsUtf8String()),
                ParseMeta()
            )
        );

        BaseProposedEvent evt = result == null
            ? new IgnoredEvent(original.EventDetails, original.LogPosition, original.SequenceNumber)
            : new ProposedEvent(
                original.EventDetails with { Stream = result.Stream, EventType = result.EventType },
                result.Data,
                result.Meta,
                original.LogPosition,
                original.SequenceNumber
            );

        return new(evt);

        JsValue? ParseMeta() {
            if (original.Metadata == null) return null;

            var metaString = original.Metadata.AsUtf8String();

            try {
                return metaString.Length == 0 ? null : parser.Parse(metaString, ParserOptions);
            } catch (Exception) {
                return null;
            }
        }
    }

    record TransformEvent(DateTimeOffset Created, string Stream, string EventType, JsValue? Data, JsValue? Meta);

    record TransformedEvent(string Stream, string EventType, byte[] Data, byte[]? Meta);
}
