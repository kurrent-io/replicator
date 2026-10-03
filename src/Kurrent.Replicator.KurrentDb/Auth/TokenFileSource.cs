namespace Kurrent.Replicator.KurrentDb.Auth;

public sealed class TokenFileSource(string path, TimeSpan reloadInterval, TimeProvider time, string side) : IAccessTokenSource {
    static ILog Log => LogProvider.GetLogger(typeof(TokenFileSource));

    readonly TokenState _state = new(time);
    readonly object     _lock  = new();

    string?         _cached;
    DateTimeOffset? _lastReadAt;
    bool            _forceReload = true;

    public ValueTask<AccessTokenLease> GetAccessToken(CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        string? value;

        lock (_lock) {
            var now = time.GetUtcNow();

            if (_forceReload || _state.HasRejection || _lastReadAt is null || now - _lastReadAt >= reloadInterval) {
                var read = TryRead();
                _lastReadAt  = now;
                _forceReload = false;

                if (read != null) {
                    _cached = read;
                }
                else if (_cached != null && !_state.IsRejected(_cached)) {
                    Log.Warn("{Side}: token file {Path} is missing or empty; keeping the previously read token", side, path);
                }
                else {
                    _cached = null;
                }
            }

            value = _cached;
        }

        if (value == null) throw new OAuthTokenException($"{side}: token file {path} is missing or empty");

        return ValueTask.FromResult(_state.Accept(value));
    }

    public void Invalidate(AccessTokenLease lease) {
        if (!_state.Invalidate(lease)) {
            Log.Debug("{Side}: ignoring stale token rejection report", side);

            return;
        }

        lock (_lock) {
            _forceReload = true;
            if (_cached == lease.Value) _cached = null;
        }
    }

    public void ReportAccepted(AccessTokenLease lease) => _state.ReportAccepted(lease);

    string? TryRead() {
        try {
            var text = File.ReadAllText(path).Trim();

            return text.Length == 0 ? null : text;
        } catch (IOException) {
            return null;
        } catch (UnauthorizedAccessException) {
            return null;
        }
    }
}
