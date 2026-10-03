namespace Kurrent.Replicator.KurrentDb.Auth;

/// <summary>Credentials for one gRPC call, plus the lease they were issued under. Both null without OAuth.</summary>
public readonly record struct CallAuth(UserCredentials? Credentials, AccessTokenLease? Lease) {
    public override string ToString() => Lease is { } lease ? $"CallAuth(Generation={lease.Generation})" : "CallAuth(none)";
}

public sealed class GrpcAuthContext {
    static ILog Log => LogProvider.GetLogger(typeof(GrpcAuthContext));

    public static GrpcAuthContext None { get; } = new(null, CancellationToken.None, TimeProvider.System, "none");

    readonly IAccessTokenSource? _source;
    readonly TokenGate           _gate;
    readonly RateLimitedWarning  _warn;

    public GrpcAuthContext(IAccessTokenSource? source, CancellationToken shutdown, TimeProvider time, string side) {
        _source  = source;
        Shutdown = shutdown;
        Time     = time;
        Side     = side;
        _gate    = new TokenGate(time, side);
        _warn    = new RateLimitedWarning(time, TimeSpan.FromSeconds(60));
    }

    public bool              Enabled  => _source != null;
    public CancellationToken Shutdown { get; }
    public TimeProvider      Time     { get; }
    public string            Side     { get; }

    public async ValueTask<CallAuth> AcquireCredentials(CancellationToken ct) {
        if (_source == null) return default;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Shutdown);
        var       lease  = await _gate.WaitForToken(_source, linked.Token).ConfigureAwait(false);

        return new(new UserCredentials(lease.Value), lease);
    }

    public void ReportAccepted(CallAuth auth) {
        if (_source != null && auth.Lease is { } lease) _source.ReportAccepted(lease);
    }

    public void ReportFailure(CallAuth auth, Exception? error) {
        if (_source != null && auth.Lease is { } lease && error != null && AuthFailure.IsTokenFailure(error)) _source.Invalidate(lease);
    }

    public async Task<T> Run<T>(Func<CallAuth, CancellationToken, Task<T>> call, CancellationToken ct) {
        if (_source == null) return await call(default, ct).ConfigureAwait(false);

        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, Shutdown);
        var       failures = 0;

        while (true) {
            var auth = await AcquireCredentials(linked.Token).ConfigureAwait(false);

            try {
                var result = await call(auth, ct).ConfigureAwait(false);
                ReportAccepted(auth);

                if (failures > 0) {
                    Log.Info("{Side}: KurrentDB accepted the access token again after {Failures} failed attempts", Side, failures);
                    _warn.Reset();
                }

                return result;
            } catch (Exception e) when (AuthFailure.IsTokenFailure(e) && !linked.IsCancellationRequested) {
                ReportFailure(auth, e);

                if (_warn.ShouldLog()) Log.Warn("{Side}: gRPC call failed with a token error ({Error}); retrying with backoff", Side, AuthFailure.Describe(e));

                await Task.Delay(TokenGate.Backoff(failures++), Time, linked.Token).ConfigureAwait(false);
            }
        }
    }

    public Task Run(Func<CallAuth, CancellationToken, Task> call, CancellationToken ct)
        => Run<bool>(async (a, c) => {
                await call(a, c).ConfigureAwait(false);

                return true;
            },
            ct
        );
}
