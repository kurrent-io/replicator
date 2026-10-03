#nullable enable
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Kurrent.Replicator.Tests.Auth.Support;

public sealed class StubTokenEndpoint : HttpMessageHandler {
    public Func<HttpRequestMessage, string, HttpResponseMessage> Respond { get; set; } = (_, _) => Json(HttpStatusCode.OK, Token("A"));

    public ConcurrentQueue<(HttpRequestMessage Request, string Body)> Requests { get; } = new();

    /// <summary>When set, every request waits for it before responding.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public int Count => Requests.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue((request, body));

        if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);

        return Respond(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // object can't have a non-null default parameter value in C#, so the common case (no expiresIn
    // supplied) is a separate overload; callers that need to omit expires_in pass expiresIn: null explicitly.
    public static string Token(string value) => Token(value, 3600, "Bearer");

    public static string Token(string value, object? expiresIn, string? tokenType = "Bearer") {
        var dict = new Dictionary<string, object?> { ["access_token"] = value };
        if (tokenType != null) dict["token_type"] = tokenType;
        if (expiresIn != null) dict["expires_in"] = expiresIn;

        return JsonSerializer.Serialize(dict);
    }

    public static Dictionary<string, string> Form(string body)
        => body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.Length > 1 ? p[1] : ""));
}
