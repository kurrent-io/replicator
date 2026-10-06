using Grpc.Core;
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Shared.Observe;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.AssertConditions.Throws;
using Ubiquitous.Metrics;
using Ubiquitous.Metrics.NoMetrics;

namespace Kurrent.Replicator.Tests.Auth;

public class GrpcEventWriterAuthTests {
    readonly FakeTimeProvider        _time     = new();
    readonly CancellationTokenSource _shutdown = new();
    readonly FakeKurrentDbHandler    _handler  = new();
    readonly string                  _file     = Path.Combine(Path.GetTempPath(), $"replicator-writer-{Guid.NewGuid():N}");

    public GrpcEventWriterAuthTests() => ReplicationMetrics.Configure(Metrics.CreateUsing(new NoMetricsProvider()));

    [After(Test)]
    public void Cleanup() {
        if (File.Exists(_file)) File.Delete(_file);
    }

    (GrpcEventWriter Writer, FallbackUsage Usage) Writer(IAccessTokenSource source) {
        FallbackUsage usage  = null!;
        var           client = FakeKurrentDbHandler.Client(_handler, configure: s => usage = GrpcAuthentication.Apply(s, source));

        return (new GrpcEventWriter(client, new GrpcAuthContext(source, _shutdown.Token, _time, "sink")), usage);
    }

    [Test]
    public async Task Every_write_kind_sends_its_own_bearer_token() {
        var source           = new ControllableTokenSource(_time) { Value = "A" };
        var (writer, usage)  = Writer(source);
        // non-transient failure for the delete: an Unavailable one would now be retried until it succeeds
        _handler.Respond     = s => Task.FromResult(s.Path == FakeKurrentDbHandler.AppendPath ? FakeKurrentDbHandler.AppendSuccess() : FakeKurrentDbHandler.TrailersOnly(StatusCode.Internal));

        await writer.WriteEvent(TestEvents.Proposed("s"), default);
        await writer.WriteEvent(TestEvents.Meta("s"), default); // metadata is an Append to $$s
        await Assert.That(async () => await writer.WriteEvent(TestEvents.Delete("s"), default)).Throws<Exception>();

        await Assert.That(_handler.Requests.Select(r => r.Authorization).Distinct().Single()).IsEqualTo("Bearer A");
        await Assert.That(_handler.AuthorizationsFor(FakeKurrentDbHandler.DeletePath)).IsNotEmpty();

        // EventStoreClient discovers server features once per channel, through the fallback path. This used to be 2
        // because the delete failed with Unavailable, which makes the client drop the channel and discover again; the
        // delete now fails with a non-transient status (Unavailable is retried), so the channel and its discovery are reused.
        await Assert.That(usage.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Write_waits_out_a_token_outage_then_sends_the_new_token() {
        var source          = new ControllableTokenSource(_time);
        var (writer, _)     = Writer(source);
        _handler.Respond    = FakeKurrentDbHandler.AcceptBearer("A");

        var write = writer.WriteEvent(TestEvents.Proposed("s"), default);
        await TimeDriver.Until(() => source.Calls >= 3, _time);
        await Assert.That(write.IsCompleted).IsFalse();
        await Assert.That(_handler.Requests).IsEmpty();

        source.Value = "A";

        // Once a real token is available, the real EventStoreClient still needs genuine wall-clock time to run its
        // (non-fake-time) server-feature discovery round-trip through the fake handler before the Append itself is
        // sent; TimeDriver.Drive's 2ms per-step real delay is sometimes too tight for that, so drive with a longer
        // real delay per step here to avoid flakiness (test-driving fix only, no production semantics involved).
        for (var i = 0; i < 2000 && !write.IsCompleted; i++) {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(15);
        }

        await write.WaitAsync(TimeSpan.FromSeconds(20));
        await Assert.That(_handler.AuthorizationsFor(FakeKurrentDbHandler.AppendPath).Single()).IsEqualTo("Bearer A");
    }

    [Test]
    public async Task Shutdown_during_outage_ends_the_write_with_cancellation() {
        var source      = new ControllableTokenSource(_time);
        var (writer, _) = Writer(source);
        var write       = writer.WriteEvent(TestEvents.Proposed("s"), CancellationToken.None);
        await TimeDriver.Until(() => source.Calls >= 2, _time);
        await _shutdown.CancelAsync();
        await Assert.That(async () => await write.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Rejected_file_token_is_replaced_by_rotated_file_without_waiting_for_reload() {
        await File.WriteAllTextAsync(_file, "A");
        var source       = new TokenFileSource(_file, TimeSpan.FromHours(1), _time, "sink");
        var (writer, _)  = Writer(source);
        _handler.Respond = FakeKurrentDbHandler.AcceptBearer("B");

        var write = writer.WriteEvent(TestEvents.Proposed("s"), default);
        await TimeDriver.Until(() => _handler.Requests.Count >= 1, _time);
        await File.WriteAllTextAsync(_file, "B");
        await TimeDriver.Drive(write, _time);

        var headers = _handler.AuthorizationsFor(FakeKurrentDbHandler.AppendPath);
        await Assert.That(headers.First()).IsEqualTo("Bearer A");
        await Assert.That(headers.Last()).IsEqualTo("Bearer B");
        await Assert.That(headers.Count(h => h == "Bearer A")).IsEqualTo(1);
    }

    [Test]
    public async Task Removed_file_after_rejection_never_resends_the_rejected_token() {
        await File.WriteAllTextAsync(_file, "A");
        var source       = new TokenFileSource(_file, TimeSpan.FromSeconds(30), _time, "sink");
        var (writer, _)  = Writer(source);
        _handler.Respond = FakeKurrentDbHandler.AcceptBearer("B");

        var write = writer.WriteEvent(TestEvents.Proposed("s"), default);
        await TimeDriver.Until(() => _handler.Requests.Count >= 1, _time);
        File.Delete(_file);
        _time.Advance(TimeSpan.FromSeconds(120)); // past quarantine: file is missing, so nothing to probe
        await Task.Delay(50);
        await File.WriteAllTextAsync(_file, "B");
        await TimeDriver.Drive(write, _time);

        var headers = _handler.AuthorizationsFor(FakeKurrentDbHandler.AppendPath);
        await Assert.That(headers.Count(h => h == "Bearer A")).IsEqualTo(1);
        await Assert.That(headers.Last()).IsEqualTo("Bearer B");
    }

    [Test]
    public async Task Server_side_fix_is_detected_by_the_probe_after_quarantine() {
        await File.WriteAllTextAsync(_file, "A");
        var source      = new TokenFileSource(_file, TimeSpan.FromSeconds(30), _time, "sink");
        var (writer, _) = Writer(source);
        var acceptA     = false;
        _handler.Respond = s => Task.FromResult(Volatile.Read(ref acceptA) ? FakeKurrentDbHandler.AppendSuccess() : FakeKurrentDbHandler.TrailersOnly(StatusCode.Unauthenticated));

        var write = writer.WriteEvent(TestEvents.Proposed("s"), default);
        await TimeDriver.Until(() => _handler.Requests.Count >= 1, _time);
        Volatile.Write(ref acceptA, true);
        await TimeDriver.Drive(write, _time);

        await Assert.That(_handler.AuthorizationsFor(FakeKurrentDbHandler.AppendPath)).IsEquivalentTo(new[] { "Bearer A", "Bearer A" });
    }

    [Test]
    public async Task Only_one_concurrent_write_probes_a_quarantined_token() {
        await File.WriteAllTextAsync(_file, "A");
        var source      = new TokenFileSource(_file, TimeSpan.FromSeconds(30), _time, "sink");
        var (writer, _) = Writer(source);

        var acceptA   = false;
        var probeHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterFlip = 0;

        _handler.Respond = async s => {
            if (!Volatile.Read(ref acceptA)) return FakeKurrentDbHandler.TrailersOnly(StatusCode.Unauthenticated);

            if (Interlocked.Increment(ref afterFlip) == 1) await probeHold.Task;

            return FakeKurrentDbHandler.AppendSuccess();
        };

        var first = writer.WriteEvent(TestEvents.Proposed("s"), default); // gets rejected -> quarantine
        await TimeDriver.Until(() => _handler.Requests.Count >= 1, _time);

        // The request being recorded only means the rejected response was sent; auth.Run's catch block still needs
        // a few real continuations to run before it calls Invalidate() and the quarantine is actually recorded.
        // Without this, "others" below can race Accept() before the quarantine lands and sail through ungated,
        // which is a test-driving race, not a production one (fix per implementer-contract guidance).
        await Task.Delay(50);

        Volatile.Write(ref acceptA, true);

        var others = Enumerable.Range(0, 5).Select(i => writer.WriteEvent(TestEvents.Proposed("s", i + 1), default)).ToList();

        await TimeDriver.Until(() => Volatile.Read(ref afterFlip) >= 1, _time);
        for (var i = 0; i < 40; i++) { _time.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(5); } // others keep backing off
        await Assert.That(Volatile.Read(ref afterFlip)).IsEqualTo(1);

        probeHold.SetResult();
        await TimeDriver.Drive(Task.WhenAll(others.Append(first)), _time);
        await Assert.That(Volatile.Read(ref afterFlip)).IsEqualTo(6);
    }
}
