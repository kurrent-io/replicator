using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;
using Serilog.Events;
using TUnit.Assertions.AssertConditions.Throws;

namespace Kurrent.Replicator.Tests.Auth;

public class TokenGateTests {
    readonly FakeTimeProvider _time = new();

    [Test]
    public async Task Backoff_doubles_and_caps_at_30_seconds() {
        var delays = Enumerable.Range(0, 8).Select(i => TokenGate.Backoff(i).TotalSeconds).ToArray();
        await Assert.That(delays).IsEquivalentTo(new double[] { 1, 2, 4, 8, 16, 30, 30, 30 });
    }

    [Test]
    public async Task Waits_until_source_recovers() {
        var source = new ControllableTokenSource(_time);
        var wait   = new TokenGate(_time, "sink").WaitForToken(source, default).AsTask();
        await TimeDriver.Until(() => source.Calls >= 3, _time);
        await Assert.That(wait.IsCompleted).IsFalse();
        source.Value = "A";
        var lease = await TimeDriver.Drive(wait, _time);
        await Assert.That(lease.Value).IsEqualTo("A");
    }

    [Test]
    public async Task Cancellation_ends_the_wait() {
        var source = new ControllableTokenSource(_time);
        using var cts = new CancellationTokenSource();
        var wait = new TokenGate(_time, "sink").WaitForToken(source, cts.Token).AsTask();
        await TimeDriver.Until(() => source.Calls >= 2, _time);
        await cts.CancelAsync();
        await Assert.That(async () => await wait).Throws<OperationCanceledException>();
    }
}

[NotInParallel("global-logger")]
public class TokenGateLoggingTests {
    [Test]
    public async Task Warnings_are_rate_limited_and_recovery_is_logged_once() {
        using var logs   = new LogCapture();
        var       time   = new FakeTimeProvider();
        var       side   = $"side-{Guid.NewGuid():N}";
        var       source = new ControllableTokenSource(time);
        var       wait   = new TokenGate(time, side).WaitForToken(source, default).AsTask();

        await TimeDriver.Until(() => time.GetUtcNow() - time.Start >= TimeSpan.FromSeconds(200), time, TimeSpan.FromSeconds(5));
        source.Value = "A";
        await TimeDriver.Drive(wait, time);

        var warnings = logs.Events.Count(e => e.Level == LogEventLevel.Warning && e.Text.Contains(side));
        await Assert.That(warnings).IsLessThanOrEqualTo(5); // first + at most once per minute over ~200s
        await Assert.That(warnings).IsGreaterThanOrEqualTo(1);
        await Assert.That(logs.Events.Count(e => e.Level == LogEventLevel.Information && e.Text.Contains(side) && e.Text.Contains("acquired again"))).IsEqualTo(1);
    }
}
