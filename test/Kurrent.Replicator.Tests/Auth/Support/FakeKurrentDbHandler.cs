#nullable enable
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using EventStore.Client;
using Grpc.Core;

namespace Kurrent.Replicator.Tests.Auth.Support;

/// <summary>
/// Stands in for KurrentDB at the HTTP/2 level: records the path and authorization header of every gRPC call
/// and answers with a configurable gRPC response. Server-feature discovery is answered with Unimplemented so the
/// client uses the classic Append RPC.
/// </summary>
public sealed class FakeKurrentDbHandler : HttpMessageHandler {
    public const string AppendPath = "/event_store.client.streams.Streams/Append";
    public const string ReadPath   = "/event_store.client.streams.Streams/Read";
    public const string DeletePath = "/event_store.client.streams.Streams/Delete";

    /// <summary>One gRPC call: path, authorization header and the raw (gRPC-framed protobuf) request body.</summary>
    public sealed record Seen(string Path, string? Authorization, byte[]? Body = null);

    public ConcurrentQueue<Seen> Requests { get; } = new();

    /// <summary>Default: every call fails with Unavailable (enough for header-capture tests).</summary>
    public Func<Seen, Task<HttpResponseMessage>> Respond { get; set; } = _ => Task.FromResult(TrailersOnly(StatusCode.Unavailable));

    public IReadOnlyList<string?> AuthorizationsFor(string path) => Requests.Where(r => r.Path == path).Select(r => r.Authorization).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        var path = request.RequestUri!.AbsolutePath;

        if (path.StartsWith("/event_store.client.server_features.", StringComparison.Ordinal)) return TrailersOnly(StatusCode.Unimplemented);

        var body = request.Content != null ? await request.Content.ReadAsByteArrayAsync(cancellationToken) : null;

        var auth = request.Headers.TryGetValues("authorization", out var values) ? values.FirstOrDefault() : null;
        var seen = new Seen(path, auth, body);
        Requests.Enqueue(seen);

        return await Respond(seen);
    }

    public static HttpResponseMessage TrailersOnly(StatusCode code) {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Version = new Version(2, 0), Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        response.Headers.Add("grpc-status", ((int)code).ToString());
        response.Headers.Add("grpc-message", code.ToString());

        return response;
    }

    /// <summary>AppendResp { success { current_revision = 0, position { commit = 1, prepare = 1 } } }</summary>
    public static HttpResponseMessage AppendSuccess() => Unary([0x0A, 0x08, 0x08, 0x00, 0x1A, 0x04, 0x08, 0x01, 0x10, 0x01]);

    static HttpResponseMessage Unary(byte[] message) {
        var frame = new byte[5 + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)message.Length);
        message.CopyTo(frame, 5);

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Version = new Version(2, 0), Content = new ByteArrayContent(frame) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        response.TrailingHeaders.Add("grpc-status", "0");

        return response;
    }

    /// <summary>Append succeeds for the listed bearer tokens and fails Unauthenticated for anything else; other calls: Unavailable / Unauthenticated.</summary>
    public static Func<Seen, Task<HttpResponseMessage>> AcceptBearer(params string[] tokens)
        => seen => {
            var ok = tokens.Any(t => seen.Authorization == $"Bearer {t}");

            return Task.FromResult(
                !ok                         ? TrailersOnly(StatusCode.Unauthenticated) :
                seen.Path == AppendPath     ? AppendSuccess() :
                                              TrailersOnly(StatusCode.Unavailable)
            );
        };

    public static EventStoreClient Client(FakeKurrentDbHandler handler, string connectionString = "esdb://localhost:2113?tls=true", Action<EventStoreClientSettings>? configure = null) {
        var settings = EventStoreClientSettings.Create(connectionString);
        settings.CreateHttpMessageHandler = () => handler;
        configure?.Invoke(settings);

        return new EventStoreClient(settings);
    }
}
