using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Kurrent.Replicator.KurrentDb.Auth;

sealed record TokenResponse(string AccessToken, TimeSpan Lifetime) {
    public override string ToString() => $"TokenResponse(Lifetime={Lifetime})";
}

sealed class TokenEndpointClient : IDisposable {
    static ILog Log => LogProvider.GetLogger(typeof(TokenEndpointClient));

    const           string   AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
    static readonly TimeSpan MaxLifetime   = TimeSpan.FromSeconds(86400);

    static readonly HashSet<string> AllowedErrors = [
        "invalid_request", "invalid_client", "invalid_grant", "unauthorized_client",
        "unsupported_grant_type", "invalid_scope", "server_error", "temporarily_unavailable"
    ];

    readonly GrpcAuthOptions _options;
    readonly string          _side;
    readonly HttpClient      _http;
    readonly Uri             _endpoint;
    readonly string?         _secret;
    readonly HashSet<string> _seenTokens = [];
    string?                  _lastAssertion;
    bool                     _clampWarned;

    public TokenEndpointClient(GrpcAuthOptions options, HttpMessageHandler handler, string side) {
        _options  = options;
        _side     = side;
        _endpoint = new Uri(options.TokenEndpoint!, UriKind.Absolute);
        _http     = new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _secret   = options.ClientSecret ?? (options.ClientSecretFile != null ? File.ReadAllText(options.ClientSecretFile).Trim() : null);
    }

    public string EndpointHost => _endpoint.Host;

    public static SocketsHttpHandler CreateDefaultHandler() => new() { AllowAutoRedirect = false };

    public async Task<TokenResponse> RequestToken(CancellationToken ct) {
        using var request = await BuildRequest(ct).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

        var status = (int)response.StatusCode;

        if (status is >= 300 and < 400)
            throw new OAuthTokenException($"{_side}: token endpoint {EndpointHost} returned HTTP {status} (redirect); redirects are not followed");

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) throw ErrorResponse(status, body);

        return Parse(body);
    }

    async Task<HttpRequestMessage> BuildRequest(CancellationToken ct) {
        var form = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };

        if (!string.IsNullOrWhiteSpace(_options.Scope)) form.Add(new("scope", _options.Scope));

        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);

        if (_options.ClientAssertionFile != null) {
            var assertion = (await File.ReadAllTextAsync(_options.ClientAssertionFile, ct).ConfigureAwait(false)).Trim();

            if (assertion.Length == 0) throw new OAuthTokenException($"{_side}: client assertion file {_options.ClientAssertionFile} is empty");

            _lastAssertion = assertion;
            form.Add(new("client_id", _options.ClientId!));
            form.Add(new("client_assertion_type", AssertionType));
            form.Add(new("client_assertion", assertion));
        }
        else if (_options.ClientAuthentication == ClientAuthenticationMethod.Basic) {
            var raw = $"{WebUtility.UrlEncode(_options.ClientId)}:{WebUtility.UrlEncode(_secret)}";
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }
        else {
            form.Add(new("client_id", _options.ClientId!));
            form.Add(new("client_secret", _secret!));
        }

        foreach (var (key, value) in _options.AdditionalParameters) form.Add(new(key, value));

        request.Content = new FormUrlEncodedContent(form);

        return request;
    }

    TokenResponse Parse(string body) {
        JsonDocument doc;

        try {
            doc = JsonDocument.Parse(body);
        } catch (JsonException) {
            throw new OAuthTokenException($"{_side}: token endpoint {EndpointHost} returned a response that is not valid JSON");
        }

        using (doc) {
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object) throw Invalid("the response is not a JSON object");

            if (!root.TryGetProperty("access_token", out var tokenEl) || tokenEl.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(tokenEl.GetString()))
                throw Invalid("access_token is missing or empty");

            if (!root.TryGetProperty("token_type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String ||
                !string.Equals(typeEl.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
                throw Invalid("token_type must be Bearer");

            var token = tokenEl.GetString()!;
            lock (_seenTokens) _seenTokens.Add(token);

            return new(token, Lifetime(root));
        }
    }

    TimeSpan Lifetime(JsonElement root) {
        if (!root.TryGetProperty("expires_in", out var el)) {
            if (_options.DefaultTokenLifetimeSeconds is { } fallback) return TimeSpan.FromSeconds(fallback);

            throw Invalid("expires_in is missing; set auth.defaultTokenLifetimeSeconds to the lifetime your provider documents");
        }

        long seconds;

        if (el.ValueKind == JsonValueKind.Number) {
            if (!el.TryGetInt64(out seconds)) throw Invalid("expires_in is not a whole number of seconds");
        }
        else if (el.ValueKind == JsonValueKind.String) {
            if (!long.TryParse(el.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds)) throw Invalid("expires_in is not a whole number of seconds");
        }
        else {
            throw Invalid("expires_in is not a number");
        }

        if (seconds <= 0) throw Invalid("expires_in must be positive");

        if (seconds > MaxLifetime.TotalSeconds) {
            if (!_clampWarned) {
                _clampWarned = true;
                Log.Warn("{Side}: token lifetime {Seconds}s exceeds 24h; treating it as 24h", _side, seconds);
            }

            return MaxLifetime;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    OAuthTokenException Invalid(string reason) => new($"{_side}: token endpoint {EndpointHost} returned an invalid token response: {reason}");

    OAuthTokenException ErrorResponse(int status, string body) {
        string? error       = null;
        string? description = null;
        var     codes       = new List<long>();

        try {
            using var doc  = JsonDocument.Parse(body);
            var       root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object) {
                if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) error = e.GetString();
                if (root.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String) description = d.GetString();

                if (root.TryGetProperty("error_codes", out var c) && c.ValueKind == JsonValueKind.Array) {
                    foreach (var item in c.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var code)) codes.Add(code);
                }
            }
        } catch (JsonException) {
            // body is not JSON: report status only
        }

        var message = new StringBuilder($"{_side}: token request to {EndpointHost} failed: HTTP {status}");

        if (error != null) message.Append(", error=").Append(AllowedErrors.Contains(error) ? error : "non-standard error (omitted)");
        if (codes.Count > 0) message.Append(", error_codes=").Append(string.Join(",", codes));

        if (description != null && Log.IsDebugEnabled()) Log.Debug("{Side}: token endpoint error_description: {Description}", _side, Redact(description));

        return new(message.ToString());
    }

    string Redact(string text) {
        var secrets = new List<string>();
        if (!string.IsNullOrEmpty(_secret)) secrets.Add(_secret);
        if (!string.IsNullOrEmpty(_lastAssertion)) secrets.Add(_lastAssertion);
        lock (_seenTokens) secrets.AddRange(_seenTokens);

        return secrets.Where(s => s.Length > 0).OrderByDescending(s => s.Length).Aggregate(text, (t, s) => t.Replace(s, "***", StringComparison.Ordinal));
    }

    public void Dispose() => _http.Dispose();
}
