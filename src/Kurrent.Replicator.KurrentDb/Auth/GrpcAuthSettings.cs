namespace Kurrent.Replicator.KurrentDb.Auth;

/// <summary>Raw `auth` section for one side, exactly as bound from YAML or environment variables.</summary>
public sealed record GrpcAuthSettings {
    public string?                    Type                        { get; init; }
    public string?                    TokenEndpoint               { get; init; }
    public string?                    ClientId                    { get; init; }
    public string?                    ClientSecret                { get; init; }
    public string?                    ClientSecretFile            { get; init; }
    public string?                    ClientAssertionFile         { get; init; }
    public string?                    ClientAuthentication        { get; init; }
    public string?                    Scope                       { get; init; }
    public Dictionary<string, string> AdditionalParameters        { get; init; } = new();
    public int?                       DefaultTokenLifetimeSeconds { get; init; }
    public int?                       RefreshBeforeExpirySeconds  { get; init; }
    public string?                    TokenFile                   { get; init; }
    public int?                       TokenFileReloadSeconds      { get; init; }

    public GrpcAuthOptions ToOptions(string side) {
        var type = (Type ?? "connectionString").Trim().ToLowerInvariant() switch {
            "connectionstring"       => GrpcAuthType.ConnectionString,
            "oauthclientcredentials" => GrpcAuthType.OAuthClientCredentials,
            "oauthtokenfile"         => GrpcAuthType.OAuthTokenFile,
            _ => throw new InvalidOperationException(
                $"Invalid {side} auth configuration: unknown auth.type '{Type}' (expected connectionString, oauthClientCredentials or oauthTokenFile)"
            )
        };

        var method = (ClientAuthentication ?? "post").Trim().ToLowerInvariant() switch {
            "post"  => ClientAuthenticationMethod.Post,
            "basic" => ClientAuthenticationMethod.Basic,
            _ => throw new InvalidOperationException(
                $"Invalid {side} auth configuration: unknown auth.clientAuthentication '{ClientAuthentication}' (expected post or basic)"
            )
        };

        var parameters = new Dictionary<string, string>();
        foreach (var (key, value) in AdditionalParameters) parameters[key.ToLowerInvariant()] = value;

        return new GrpcAuthOptions {
            Type                        = type,
            TokenEndpoint               = NullIfEmpty(TokenEndpoint),
            ClientId                    = NullIfEmpty(ClientId),
            ClientSecret                = NullIfEmpty(ClientSecret),
            ClientSecretFile            = NullIfEmpty(ClientSecretFile),
            ClientAssertionFile         = NullIfEmpty(ClientAssertionFile),
            ClientAuthentication        = method,
            Scope                       = NullIfEmpty(Scope),
            AdditionalParameters        = parameters,
            DefaultTokenLifetimeSeconds = DefaultTokenLifetimeSeconds,
            RefreshBeforeExpirySeconds  = RefreshBeforeExpirySeconds ?? 300,
            TokenFile                   = NullIfEmpty(TokenFile),
            TokenFileReloadSeconds      = TokenFileReloadSeconds ?? 30
        };
    }

    static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    public override string ToString() => $"GrpcAuthSettings(Type={Type})";
}
