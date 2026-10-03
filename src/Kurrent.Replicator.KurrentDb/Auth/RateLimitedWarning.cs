namespace Kurrent.Replicator.KurrentDb.Auth;

/// <summary>Returns true the first time and then at most once per interval.</summary>
public sealed class RateLimitedWarning(TimeProvider time, TimeSpan interval) {
    readonly object  _lock = new();
    DateTimeOffset? _last;

    public bool ShouldLog() {
        lock (_lock) {
            var now = time.GetUtcNow();

            if (_last is { } last && now - last < interval) return false;

            _last = now;

            return true;
        }
    }

    public void Reset() {
        lock (_lock) _last = null;
    }
}
