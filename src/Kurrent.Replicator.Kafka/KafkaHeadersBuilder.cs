// Copyright (c) Kurrent, Inc and/or licensed to Kurrent, Inc under one or more agreements.
// Kurrent, Inc licenses this file to you under the Kurrent License v1 (see LICENSE.md).

using System.Text;
using System.Text.Json;
using Kurrent.Replicator.Shared.Contracts;
using Kurrent.Replicator.Shared.Logging;

namespace Kurrent.Replicator.Kafka;

public static class KafkaHeadersBuilder {
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();
    
    public static Headers? BuildHeaders(string contentType, byte[]? metadata) {
        if (contentType != ContentTypes.Json || metadata is not { Length: > 0 })
            return null;

        try {
            using var doc  = JsonDocument.Parse(metadata);
            var       root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var headers = new Headers();
            foreach (var prop in root.EnumerateObject()) {
                var valueBytes = prop.Value.ValueKind switch {
                    JsonValueKind.String => Encoding.UTF8.GetBytes(prop.Value.GetString() ?? string.Empty),
                    _                    => Encoding.UTF8.GetBytes(prop.Value.GetRawText())
                };
                headers.Add(prop.Name, valueBytes);
            }

            return headers.Count > 0 ? headers : null;
        } catch (JsonException e) {
            Log.Warn("Malformed json, skipping metadata to header mapping", e);
            return null;
        }
    }
}
