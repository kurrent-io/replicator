using System.Text.Json;
using Kurrent.Replicator.Shared.Contracts;

namespace Kurrent.Replicator.Shared.Pipeline;

public delegate ValueTask<BaseProposedEvent> TransformEvent(OriginalEvent originalEvent, CancellationToken cancellationToken);

public static class Transforms {
    public static ValueTask<BaseProposedEvent> DefaultWithExtraMeta(OriginalEvent originalEvent, CancellationToken _) {
        var proposed =
            new ProposedEvent(
                originalEvent.EventDetails,
                originalEvent.Data,
                AddMeta(),
                originalEvent.LogPosition,
                originalEvent.SequenceNumber
            );

        return new(proposed);

        byte[] AddMeta() {
            if (originalEvent.Metadata == null || originalEvent.Metadata.Length == 0) {
                var eventMeta = new EventMetadata {
                    OriginalEventNumber = originalEvent.LogPosition.EventNumber,
                    OriginalPosition    = originalEvent.LogPosition.EventPosition,
                    OriginalCreatedDate = originalEvent.Created
                };

                return JsonSerializer.SerializeToUtf8Bytes(eventMeta);
            }

            using var stream       = new MemoryStream();
            using var writer       = new Utf8JsonWriter(stream);
            using var originalMeta = JsonDocument.Parse(originalEvent.Metadata);

            writer.WriteStartObject();

            foreach (var jsonElement in originalMeta.RootElement.EnumerateObject()) {
                jsonElement.WriteTo(writer);
            }

            writer.WriteNumber(EventMetadata.EventNumberPropertyName, originalEvent.LogPosition.EventNumber);
            writer.WriteNumber(EventMetadata.PositionPropertyName, originalEvent.LogPosition.EventPosition);
            writer.WriteString(EventMetadata.CreatedDate, originalEvent.Created);
            writer.WriteEndObject();
            writer.Flush();

            return stream.ToArray();
        }
    }
}
