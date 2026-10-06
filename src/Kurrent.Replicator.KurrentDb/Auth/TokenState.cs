namespace Kurrent.Replicator.KurrentDb.Auth;

/// <summary>
/// Generation, rejected-token quarantine and probe lease, shared by both token sources.
/// All members are synchronous and hold only this object's lock.
/// </summary>
sealed class TokenState(TimeProvider time) {
    public static readonly TimeSpan Quarantine        = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ProbeLeaseTimeout = TimeSpan.FromSeconds(60);

    readonly object _lock = new();

    string?        _current;
    long           _generation;
    string?        _rejected;
    DateTimeOffset _rejectedAt;
    bool           _probeHeld;
    DateTimeOffset _probeGrantedAt;

    public long Generation { get { lock (_lock) return _generation; } }

    public bool HasRejection { get { lock (_lock) return _rejected != null; } }

    public bool IsRejected(string value) {
        lock (_lock) return _rejected == value;
    }

    public AccessTokenLease Accept(string value) {
        lock (_lock) {
            var now = time.GetUtcNow();
            ExpireProbe(now);

            if (_rejected != null && value == _rejected) {
                if (now - _rejectedAt < Quarantine)
                    throw new OAuthTokenException("The token source returned a token the server just rejected; waiting for a new token");

                if (_probeHeld)
                    throw new OAuthTokenException("A previously rejected token is being re-tested by another call; waiting for the result");

                _probeHeld      = true;
                _probeGrantedAt = now;
                _current        = value;
                _generation++;

                return new(value, _generation);
            }

            if (_rejected != null) {
                _rejected  = null;
                _probeHeld = false;
            }

            if (value != _current) {
                _current = value;
                _generation++;
            }

            return new(value, _generation);
        }
    }

    public bool Invalidate(AccessTokenLease lease) {
        lock (_lock) {
            if (lease.Generation != _generation) return false;

            _rejected   = lease.Value;
            _rejectedAt = time.GetUtcNow();
            _probeHeld  = false;
            _current    = null;
            _generation++;

            return true;
        }
    }

    public bool ReportAccepted(AccessTokenLease lease) {
        lock (_lock) {
            if (lease.Generation != _generation) return false;

            if (_probeHeld && _rejected == lease.Value) {
                _rejected  = null;
                _probeHeld = false;
                _generation++;
            }

            return true;
        }
    }

    void ExpireProbe(DateTimeOffset now) {
        if (!_probeHeld || now - _probeGrantedAt < ProbeLeaseTimeout) return;

        _probeHeld  = false;
        _rejectedAt = now; // expiry counts as a new quarantine
        _current    = null;
        _generation++;
    }
}
