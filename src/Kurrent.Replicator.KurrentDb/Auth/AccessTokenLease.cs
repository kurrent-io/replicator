namespace Kurrent.Replicator.KurrentDb.Auth;

/// <summary>A token value handed to exactly one call, tagged with the source generation it was issued under.</summary>
public readonly record struct AccessTokenLease(string Value, long Generation) {
    public override string ToString() => $"AccessTokenLease(Generation={Generation})";
}

public interface IAccessTokenSource {
    ValueTask<AccessTokenLease> GetAccessToken(CancellationToken ct);

    /// <summary>KurrentDB rejected the token carried by this lease. Synchronous, no I/O, never calls out.</summary>
    void Invalidate(AccessTokenLease lease);

    /// <summary>A call carrying this lease succeeded. Synchronous, no I/O, never calls out.</summary>
    void ReportAccepted(AccessTokenLease lease);
}
