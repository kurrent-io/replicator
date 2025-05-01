using System.Text.Json;

namespace Kurrent.Replicator.KurrentDb.Internals;

public static class MetaSerialization {
    internal static readonly JsonSerializerOptions StreamMetadataJsonSerializerOptions = new() {
        Converters = { StreamMetadataJsonConverter.Instance },
    };
}
