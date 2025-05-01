using System.Net;
using System.Text;
using System.Text.Json;
using Kurrent.Replicator.Shared.Contracts;

namespace Kurrent.Replicator.Http;

public class HttpTransform {
    readonly HttpClient _client;

    public HttpTransform(string? url) {
        ArgumentException.ThrowIfNullOrEmpty(url);
        _client = new() { BaseAddress = new(url) };
    }

    public async ValueTask<BaseProposedEvent> Transform(OriginalEvent originalEvent, CancellationToken cancellationToken) {
        var httpEvent = new HttpEvent(
            originalEvent.EventDetails.EventType,
            originalEvent.EventDetails.Stream,
            Encoding.UTF8.GetString(originalEvent.Data),
            originalEvent.Metadata == null ? null : Encoding.UTF8.GetString(originalEvent.Metadata)
        );

        try {
            var response = await _client.PostAsync("", new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(httpEvent)), cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) {
                throw new HttpRequestException($"Transformation request failed: {response.ReasonPhrase}");
            }

            if (response.StatusCode == HttpStatusCode.NoContent) {
                return new IgnoredEvent(originalEvent.EventDetails, originalEvent.LogPosition, originalEvent.SequenceNumber);
            }

            var httpResponse = (await JsonSerializer.DeserializeAsync<HttpEvent>(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken
                )
                .ConfigureAwait(false))!;

            return new ProposedEvent(
                originalEvent.EventDetails with {
                    EventType = httpResponse.EventType, Stream = httpResponse.StreamName
                },
                Encoding.UTF8.GetBytes(httpResponse.Payload),
                httpResponse.Metadata == null ? originalEvent.Metadata : Encoding.UTF8.GetBytes(httpResponse.Metadata),
                originalEvent.LogPosition,
                originalEvent.SequenceNumber
            );
        } catch (OperationCanceledException) {
            return new NoEvent(originalEvent.EventDetails, originalEvent.LogPosition, originalEvent.SequenceNumber);
        }
    }

    record HttpEvent(string EventType, string StreamName, string Payload, string? Metadata);
}
