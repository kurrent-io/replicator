namespace Kurrent.Replicator.KurrentDb.Auth;

public sealed class TokenGate(TimeProvider time, string side) {
    static ILog Log => LogProvider.GetLogger(typeof(TokenGate));

    static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    readonly RateLimitedWarning _warn = new(time, TimeSpan.FromSeconds(60));

    public static TimeSpan Backoff(int attempt) {
        var seconds = attempt >= 5 ? MaxBackoff.TotalSeconds : Math.Pow(2, attempt);

        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    public async ValueTask<AccessTokenLease> WaitForToken(IAccessTokenSource source, CancellationToken ct) {
        var failures = 0;

        while (true) {
            ct.ThrowIfCancellationRequested();

            try {
                var lease = await source.GetAccessToken(ct).ConfigureAwait(false);

                if (failures > 0) {
                    Log.Info("{Side}: access token acquired again after {Failures} failed attempts", side, failures);
                    _warn.Reset();
                }

                return lease;
            } catch (OAuthTokenException e) {
                if (_warn.ShouldLog()) Log.Warn("{Side}: no access token available ({Reason}); retrying with backoff", side, e.Message);

                await Task.Delay(Backoff(failures++), time, ct).ConfigureAwait(false);
            }
        }
    }
}
