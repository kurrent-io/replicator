using Grpc.Core;

namespace Kurrent.Replicator.KurrentDb.Auth;

public static class AuthFailure {
    /// <summary>Token missing, expired or rejected: retrying with a (new) token may help.</summary>
    public static bool IsTokenFailure(Exception e)
        => Chain(e).Any(x => x is OAuthTokenException or NotAuthenticatedException or RpcException { StatusCode: StatusCode.Unauthenticated });

    /// <summary>Authorization decision: a new token does not help.</summary>
    public static bool IsPermissionDenied(Exception e)
        => Chain(e).Any(x => x is AccessDeniedException or RpcException { StatusCode: StatusCode.PermissionDenied });

    /// <summary>
    /// A fixed, safe description of an authentication failure for logging. Never includes a server-supplied
    /// status detail or an arbitrary exception message, both of which a malicious or misbehaving gRPC server
    /// could use to smuggle a token (e.g. an echoed Authorization header) into the logs.
    /// </summary>
    public static string Describe(Exception e) {
        if (Chain(e).OfType<OAuthTokenException>().FirstOrDefault() is { } oauth) return oauth.Message;

        if (IsTokenFailure(e)) return "KurrentDB rejected the access token (Unauthenticated)";

        var status = Chain(e).OfType<RpcException>().FirstOrDefault()?.StatusCode;

        return status is { } code ? $"{e.GetType().Name} ({code})" : e.GetType().Name;
    }

    internal static IEnumerable<Exception> Chain(Exception root) {
        var pending = new Stack<Exception>();
        pending.Push(root);
        var visited = 0;

        while (pending.Count > 0 && visited++ < 64) {
            var e = pending.Pop();

            yield return e;

            if (e is AggregateException agg) {
                foreach (var inner in agg.InnerExceptions) pending.Push(inner);
            }
            else if (e.InnerException is { } inner) {
                pending.Push(inner);
            }

            if (e is RpcException rpc && rpc.Status.DebugException is { } debug) pending.Push(debug);
        }
    }
}
