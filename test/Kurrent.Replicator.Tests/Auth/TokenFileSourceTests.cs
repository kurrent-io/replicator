using Kurrent.Replicator.KurrentDb.Auth;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.AssertConditions.Throws;

namespace Kurrent.Replicator.Tests.Auth;

public class TokenFileSourceTests {
    readonly FakeTimeProvider _time = new();
    readonly string           _path = Path.Combine(Path.GetTempPath(), $"replicator-token-{Guid.NewGuid():N}");

    TokenFileSource NewSource(int reloadSeconds = 30) => new(_path, TimeSpan.FromSeconds(reloadSeconds), _time, "sink");

    [After(Test)]
    public void Cleanup() {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Test]
    public async Task Reads_and_trims() {
        await File.WriteAllTextAsync(_path, "  A \n");
        var lease = await NewSource().GetAccessToken(default);
        await Assert.That(lease.Value).IsEqualTo("A");
    }

    [Test]
    public async Task Trailing_newline_is_trimmed() {
        await File.WriteAllTextAsync(_path, "eyJhbGciOi.abc.def\r\n");
        await Assert.That((await NewSource().GetAccessToken(default)).Value).IsEqualTo("eyJhbGciOi.abc.def");
    }

    [Test]
    public async Task Missing_file_without_previous_token_throws() {
        await Assert.That(async () => await NewSource().GetAccessToken(default)).Throws<OAuthTokenException>();
    }

    [Test]
    public async Task Does_not_reread_before_interval() {
        await File.WriteAllTextAsync(_path, "A");
        var source = NewSource();
        await source.GetAccessToken(default);
        await File.WriteAllTextAsync(_path, "B");
        _time.Advance(TimeSpan.FromSeconds(29));
        await Assert.That((await source.GetAccessToken(default)).Value).IsEqualTo("A");
        _time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That((await source.GetAccessToken(default)).Value).IsEqualTo("B");
    }

    [Test]
    public async Task Empty_file_mid_rotation_keeps_previous_token() {
        await File.WriteAllTextAsync(_path, "A");
        var source = NewSource();
        await source.GetAccessToken(default);
        await File.WriteAllTextAsync(_path, "");
        _time.Advance(TimeSpan.FromSeconds(30));
        await Assert.That((await source.GetAccessToken(default)).Value).IsEqualTo("A");
        File.Delete(_path);
        _time.Advance(TimeSpan.FromSeconds(30));
        await Assert.That((await source.GetAccessToken(default)).Value).IsEqualTo("A");
    }

    [Test]
    public async Task Invalidate_forces_immediate_reread_and_accepts_new_value() {
        await File.WriteAllTextAsync(_path, "A");
        var source = NewSource();
        var a      = await source.GetAccessToken(default);
        await File.WriteAllTextAsync(_path, "B");
        source.Invalidate(a);
        await Assert.That((await source.GetAccessToken(default)).Value).IsEqualTo("B");
    }

    [Test]
    public async Task After_invalidate_missing_or_same_value_throws_during_quarantine_then_probes() {
        await File.WriteAllTextAsync(_path, "A");
        var source = NewSource();
        source.Invalidate(await source.GetAccessToken(default));

        await Assert.That(async () => await source.GetAccessToken(default)).Throws<OAuthTokenException>(); // still A
        File.Delete(_path);
        await Assert.That(async () => await source.GetAccessToken(default)).Throws<OAuthTokenException>(); // missing, no fallback to A

        await File.WriteAllTextAsync(_path, "A");
        _time.Advance(TimeSpan.FromSeconds(61));
        await Assert.That((await source.GetAccessToken(default)).Value).IsEqualTo("A"); // probe
    }

    [Test]
    public async Task Same_value_reload_does_not_lose_rejection() {
        await File.WriteAllTextAsync(_path, "A");
        var source = NewSource();
        var call1  = await source.GetAccessToken(default);
        _time.Advance(TimeSpan.FromSeconds(30));
        await source.GetAccessToken(default); // timer re-read of unchanged A
        source.Invalidate(call1);
        await Assert.That(async () => await source.GetAccessToken(default)).Throws<OAuthTokenException>();
    }

    [Test]
    public async Task Honours_cancellation() {
        await File.WriteAllTextAsync(_path, "A");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.That(async () => await NewSource().GetAccessToken(cts.Token)).Throws<OperationCanceledException>();
    }
}
