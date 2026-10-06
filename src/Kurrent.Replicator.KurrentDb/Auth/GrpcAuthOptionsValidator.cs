namespace Kurrent.Replicator.KurrentDb.Auth;

public static class GrpcAuthOptionsValidator {
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) {
        "grant_type", "client_id", "client_secret", "client_assertion", "client_assertion_type", "scope"
    };

    public static IReadOnlyList<string> Validate(GrpcAuthOptions o, EventStoreClientSettings settings) {
        var errors = new List<string>();

        if (!o.IsOAuth) return errors;

        if (settings.DefaultCredentials != null)
            errors.Add("the connection string contains credentials (user:pass@); remove them when using OAuth");

        if (settings.ConnectivitySettings.Insecure)
            errors.Add("tls must be enabled for OAuth (bearer tokens are not sent over insecure channels)");

        if (o.Type == GrpcAuthType.OAuthClientCredentials) ValidateClientCredentials(o, errors);
        else ValidateTokenFile(o, errors);

        return errors;
    }

    static void ValidateClientCredentials(GrpcAuthOptions o, List<string> errors) {
        if (string.IsNullOrWhiteSpace(o.TokenEndpoint)) {
            errors.Add("tokenEndpoint is required");
        }
        else if (!Uri.TryCreate(o.TokenEndpoint, UriKind.Absolute, out var uri) ||
                 !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))) {
            errors.Add("tokenEndpoint must be an absolute https URL (http is only allowed for loopback hosts)");
        }

        if (string.IsNullOrWhiteSpace(o.ClientId)) errors.Add("clientId is required");

        var credentialCount = new[] { o.ClientSecret, o.ClientSecretFile, o.ClientAssertionFile }.Count(x => !string.IsNullOrEmpty(x));

        if (credentialCount != 1) errors.Add("exactly one of clientSecret, clientSecretFile or clientAssertionFile must be set");

        if (!string.IsNullOrEmpty(o.ClientSecretFile)) ValidateSecretFile(o.ClientSecretFile, errors);

        if (!string.IsNullOrEmpty(o.ClientAssertionFile) && !File.Exists(o.ClientAssertionFile))
            errors.Add($"clientAssertionFile {o.ClientAssertionFile} does not exist");

        if (o.RefreshBeforeExpirySeconds < 0) errors.Add("refreshBeforeExpirySeconds must be >= 0");

        if (o.DefaultTokenLifetimeSeconds is <= 0) errors.Add("defaultTokenLifetimeSeconds must be > 0");

        var reserved = o.AdditionalParameters.Keys.Where(Reserved.Contains).ToList();

        if (reserved.Count > 0) errors.Add($"additionalParameters must not override {string.Join(", ", reserved)}");
    }

    static void ValidateSecretFile(string path, List<string> errors) {
        if (!File.Exists(path)) {
            errors.Add($"clientSecretFile {path} does not exist or is empty");

            return;
        }

        try {
            if (File.ReadAllText(path).Trim().Length == 0) errors.Add($"clientSecretFile {path} does not exist or is empty");
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            errors.Add($"clientSecretFile {path} cannot be read ({e.GetType().Name})");
        }
    }

    static void ValidateTokenFile(GrpcAuthOptions o, List<string> errors) {
        if (string.IsNullOrWhiteSpace(o.TokenFile)) errors.Add("tokenFile is required");

        if (o.TokenFileReloadSeconds <= 0) errors.Add("tokenFileReloadSeconds must be > 0");
    }

    public static IReadOnlyList<string> Warnings(GrpcAuthOptions o) {
        if (o.IsOAuth) return [];

        var set = new List<string>();
        if (o.TokenEndpoint != null) set.Add("tokenEndpoint");
        if (o.ClientId != null) set.Add("clientId");
        if (o.ClientSecret != null) set.Add("clientSecret");
        if (o.ClientSecretFile != null) set.Add("clientSecretFile");
        if (o.ClientAssertionFile != null) set.Add("clientAssertionFile");
        if (o.Scope != null) set.Add("scope");
        if (o.AdditionalParameters.Count > 0) set.Add("additionalParameters");
        if (o.DefaultTokenLifetimeSeconds != null) set.Add("defaultTokenLifetimeSeconds");
        if (o.TokenFile != null) set.Add("tokenFile");

        return set.Count == 0 ? [] : [$"auth options {string.Join(", ", set)} are ignored because auth.type is connectionString"];
    }

    public static string? ValidateProtocol(GrpcAuthOptions o, string? protocol)
        => o.IsOAuth && !string.Equals(protocol, "grpc", StringComparison.OrdinalIgnoreCase)
            ? $"auth.type {o.Type} is only supported with protocol grpc (configured: {protocol ?? "none"})"
            : null;

    public static void EnsureValid(string side, GrpcAuthOptions o, EventStoreClientSettings settings) {
        var errors = Validate(o, settings);

        if (errors.Count > 0) throw new InvalidOperationException($"Invalid {side} auth configuration: {string.Join("; ", errors)}");
    }
}
