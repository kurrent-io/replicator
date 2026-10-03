using Kurrent.Replicator.KurrentDb.Auth;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.AssertConditions.Throws;

namespace Kurrent.Replicator.Tests.Auth;

public class TokenStateTests {
    readonly FakeTimeProvider _time = new();
    TokenState NewState() => new(_time);

    [Test]
    public async Task Same_value_does_not_bump_generation() {
        var s = NewState();
        var a1 = s.Accept("A");
        var a2 = s.Accept("A");
        await Assert.That(a2.Generation).IsEqualTo(a1.Generation);
    }

    [Test]
    public async Task New_value_bumps_generation() {
        var s = NewState();
        var a = s.Accept("A");
        var b = s.Accept("B");
        await Assert.That(b.Generation).IsGreaterThan(a.Generation);
    }

    [Test]
    public async Task Invalidate_current_starts_quarantine() {
        var s = NewState();
        var a = s.Accept("A");
        await Assert.That(s.Invalidate(a)).IsTrue();
        await Assert.That(() => { s.Accept("A"); }).Throws<OAuthTokenException>();
        _time.Advance(TimeSpan.FromSeconds(59));
        await Assert.That(() => { s.Accept("A"); }).Throws<OAuthTokenException>();
    }

    [Test]
    public async Task Different_value_is_accepted_immediately_and_clears_rejection() {
        var s = NewState();
        s.Invalidate(s.Accept("A"));
        var b = s.Accept("B");
        await Assert.That(b.Value).IsEqualTo("B");
        await Assert.That(s.HasRejection).IsFalse();
    }

    [Test]
    public async Task Same_value_reload_does_not_lose_rejection() {
        var s = NewState();
        var call1 = s.Accept("A");
        s.Accept("A"); // routine reload / reissue of the same value
        s.Accept("A");
        await Assert.That(s.Invalidate(call1)).IsTrue();
        await Assert.That(() => { s.Accept("A"); }).Throws<OAuthTokenException>();
    }

    [Test]
    public async Task After_quarantine_exactly_one_probe_gets_the_value() {
        var s = NewState();
        s.Invalidate(s.Accept("A"));
        _time.Advance(TimeSpan.FromSeconds(61));

        var results = Enumerable.Range(0, 10).Select(_ => {
            try { return (AccessTokenLease?)s.Accept("A"); } catch (OAuthTokenException) { return null; }
        }).ToList();

        await Assert.That(results.Count(r => r is not null)).IsEqualTo(1);
    }

    [Test]
    public async Task Accepted_probe_releases_value_to_everyone() {
        var s = NewState();
        s.Invalidate(s.Accept("A"));
        _time.Advance(TimeSpan.FromSeconds(61));
        var probe = s.Accept("A");
        await Assert.That(s.ReportAccepted(probe)).IsTrue();
        await Assert.That(s.Accept("A").Value).IsEqualTo("A");
        await Assert.That(s.HasRejection).IsFalse();
    }

    [Test]
    public async Task Rejected_probe_restarts_quarantine() {
        var s = NewState();
        s.Invalidate(s.Accept("A"));
        _time.Advance(TimeSpan.FromSeconds(61));
        var probe = s.Accept("A");
        await Assert.That(s.Invalidate(probe)).IsTrue();
        _time.Advance(TimeSpan.FromSeconds(30));
        await Assert.That(() => { s.Accept("A"); }).Throws<OAuthTokenException>();
    }

    [Test]
    public async Task Unreported_probe_lease_expires_and_counts_as_new_quarantine() {
        var s = NewState();
        s.Invalidate(s.Accept("A"));
        _time.Advance(TimeSpan.FromSeconds(61));
        var probe = s.Accept("A");
        _time.Advance(TimeSpan.FromSeconds(61)); // lease expired -> new quarantine starts now
        await Assert.That(() => { s.Accept("A"); }).Throws<OAuthTokenException>();
        await Assert.That(s.ReportAccepted(probe)).IsFalse(); // stale after expiry
        _time.Advance(TimeSpan.FromSeconds(61));
        await Assert.That(s.Accept("A").Value).IsEqualTo("A"); // next probe
    }

    [Test]
    public async Task Stale_reports_are_ignored() {
        var s = NewState();
        var call1 = s.Accept("A");                 // gen 1
        s.Invalidate(s.Accept("A"));               // another call rejects -> quarantine, gen 2
        await Assert.That(s.ReportAccepted(call1)).IsFalse(); // late success from gen 1 does not clear quarantine
        await Assert.That(() => { s.Accept("A"); }).Throws<OAuthTokenException>();

        _time.Advance(TimeSpan.FromSeconds(61));
        var probe = s.Accept("A");                 // gen 3
        s.ReportAccepted(probe);                   // gen 4
        await Assert.That(s.Invalidate(call1)).IsFalse(); // late rejection from gen 1 ignored
        await Assert.That(s.Accept("A").Value).IsEqualTo("A");
    }

    [Test]
    public async Task IsRejected_reports_the_quarantined_value() {
        var s = NewState();
        s.Invalidate(s.Accept("A"));
        await Assert.That(s.IsRejected("A")).IsTrue();
        await Assert.That(s.IsRejected("B")).IsFalse();
    }
}
