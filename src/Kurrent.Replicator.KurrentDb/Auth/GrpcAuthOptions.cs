namespace Kurrent.Replicator.KurrentDb.Auth;

public enum GrpcAuthType { ConnectionString, OAuthClientCredentials, OAuthTokenFile }

public enum ClientAuthenticationMethod { Post, Basic }

public sealed record GrpcAuthOptions {
    public static GrpcAuthOptions Default { get; } = new();

    public GrpcAuthType Type { get; init; } = GrpcAuthType.ConnectionString;

    // oauthClientCredentials
    public string?                             TokenEndpoint               { get; init; }
    public string?                             ClientId                    { get; init; }
    public string?                             ClientSecret                { get; init; }
    public string?                             ClientSecretFile            { get; init; }
    public string?                             ClientAssertionFile         { get; init; }
    public ClientAuthenticationMethod          ClientAuthentication        { get; init; } = ClientAuthenticationMethod.Post;
    public string?                             Scope                       { get; init; }
    public IReadOnlyDictionary<string, string> AdditionalParameters        { get; init; } = new Dictionary<string, string>();
    public int?                                DefaultTokenLifetimeSeconds { get; init; }
    public int                                 RefreshBeforeExpirySeconds  { get; init; } = 300;

    // oauthTokenFile
    public string? TokenFile              { get; init; }
    public int     TokenFileReloadSeconds { get; init; } = 30;

    public bool IsOAuth => Type != GrpcAuthType.ConnectionString;

    public override string ToString() => $"GrpcAuthOptions(Type={Type})"; // never print secrets
}
