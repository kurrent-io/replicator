namespace Kurrent.Replicator.KurrentDb.Auth;

public sealed class FallbackUsage {
    int _count;

    public int Count => Volatile.Read(ref _count);

    internal void Increment() => Interlocked.Increment(ref _count);
}

public static class GrpcAuthentication {
    static ILog Log => LogProvider.GetLogger(typeof(GrpcAuthentication));

    /// <summary>Marks "no per-call credentials were passed"; never sent as-is.</summary>
    internal static readonly UserCredentials Sentinel = new("replicator-oauth-sentinel");

    public static FallbackUsage Apply(EventStoreClientSettings settings, IAccessTokenSource source) {
        var usage = new FallbackUsage();
        settings.DefaultCredentials = Sentinel;

        settings.OperationOptions.GetAuthenticationHeaderValue = async (credentials, ct) => {
            if (!ReferenceEquals(credentials, Sentinel)) return credentials.ToString();

            usage.Increment();
            Log.Debug("gRPC call made without per-call credentials; using the fallback token");
            var lease = await source.GetAccessToken(ct).ConfigureAwait(false);

            return $"Bearer {lease.Value}";
        };

        return usage;
    }

    public static IAccessTokenSource? CreateSource(GrpcAuthOptions options, string side, TimeProvider time, CancellationToken shutdown)
        => options.Type switch {
            GrpcAuthType.ConnectionString       => null,
            GrpcAuthType.OAuthClientCredentials => new ClientCredentialsTokenSource(options, side, TokenEndpointClient.CreateDefaultHandler(), time, shutdown),
            GrpcAuthType.OAuthTokenFile         => new TokenFileSource(options.TokenFile!, TimeSpan.FromSeconds(options.TokenFileReloadSeconds), time, side),
            _                                   => throw new ArgumentOutOfRangeException(nameof(options), options.Type, "Unknown auth type")
        };
}
