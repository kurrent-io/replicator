using System.Net.Sockets;
using System.Security.Authentication;
using Grpc.Core;

namespace Kurrent.Replicator.KurrentDb;

/// <summary>
/// Classifies failures of a KurrentDB call that are worth retrying as-is: the node is unreachable, restarting,
/// overloaded or changing leader. A write whose response was lost may have landed, so a retry must be idempotent:
/// events and stream metadata are appended with <see cref="StreamState.Any"/> and the source event's id (metadata as a
/// <c>$metadata</c> event on <c>$$stream</c>, not via SetStreamMetadataAsync, which mints a new id per call), so
/// KurrentDB deduplicates the repeat. A repeated soft delete with <see cref="StreamState.Any"/> succeeds and leaves
/// the stream deleted.
/// </summary>
public static class TransientFailure {
    public static bool IsTransient(Exception e)
        => !IsPermanentSetupFailure(e) && AuthFailure.Chain(e).Any(
            x => x is RpcException {
                    StatusCode: StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.ResourceExhausted or StatusCode.Aborted
                }
                or NotLeaderException
                or HttpRequestException
                or IOException
                or SocketException
        );

    /// <summary>
    /// Connection setup failures that retrying cannot fix, even though they surface as an
    /// <see cref="HttpRequestException"/> or an Unavailable status: TLS certificate or hostname validation failed
    /// (wrong <c>tlsCaFile</c>, untrusted or mismatched certificate), an unsupported scheme or platform feature, a
    /// malformed address, or an endpoint that does not speak HTTP/2. These must fail the write so the host stops.
    /// </summary>
    static bool IsPermanentSetupFailure(Exception e)
        => AuthFailure.Chain(e).Any(
            x => x is AuthenticationException
                or NotSupportedException
                or UriFormatException
                or HttpRequestException { HttpRequestError: HttpRequestError.VersionNegotiationError }
        );

    /// <summary>
    /// Exception type plus gRPC status code. Never includes the status detail or exception messages, which can
    /// carry server-supplied text.
    /// </summary>
    public static string Describe(Exception e) => AuthFailure.Describe(e);
}
