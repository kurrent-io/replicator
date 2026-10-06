#nullable enable
using System.Collections.Concurrent;
using Kurrent.Replicator.KurrentDb.Auth;

namespace Kurrent.Replicator.Tests.Auth.Support;

/// <summary>A token source whose current value the test controls, with the real TokenState rules.</summary>
public sealed class ControllableTokenSource(TimeProvider time) : IAccessTokenSource {
    readonly TokenState _state = new(time);
    int                 _calls;

    public volatile string? Value;

    public ConcurrentQueue<(AccessTokenLease Lease, bool Applied)> Invalidated { get; } = new();
    public ConcurrentQueue<AccessTokenLease>                       Accepted    { get; } = new();
    public Action<AccessTokenLease>?                               OnInvalidate { get; set; }
    public Action<AccessTokenLease>?                               OnAccepted   { get; set; }

    public int  Calls      => Volatile.Read(ref _calls);
    public long Generation => _state.Generation;

    public ValueTask<AccessTokenLease> GetAccessToken(CancellationToken ct) {
        Interlocked.Increment(ref _calls);
        ct.ThrowIfCancellationRequested();
        var value = Value ?? throw new OAuthTokenException("test: token unavailable");

        return ValueTask.FromResult(_state.Accept(value));
    }

    public void Invalidate(AccessTokenLease lease) {
        var applied = _state.Invalidate(lease);
        Invalidated.Enqueue((lease, applied));
        OnInvalidate?.Invoke(lease);
    }

    public void ReportAccepted(AccessTokenLease lease) {
        Accepted.Enqueue(lease);
        _state.ReportAccepted(lease);
        OnAccepted?.Invoke(lease);
    }
}
