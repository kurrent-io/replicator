using Grpc.Core;
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Shared;
using Kurrent.Replicator.Shared.Observe;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.AssertConditions.Throws;
using Ubiquitous.Metrics;
using Ubiquitous.Metrics.NoMetrics;

namespace Kurrent.Replicator.Tests.Auth;

public class GrpcEventReaderAuthTests {
    readonly FakeTimeProvider     _time    = new();
    readonly FakeKurrentDbHandler _handler = new();
    readonly string               _file    = Path.Combine(Path.GetTempPath(), $"replicator-reader-{Guid.NewGuid():N}");

    public GrpcEventReaderAuthTests() => ReplicationMetrics.Configure(Metrics.CreateUsing(new NoMetricsProvider()));

    [After(Test)]
    public void Cleanup() {
        if (File.Exists(_file)) File.Delete(_file);
    }

    FallbackUsage _usage = null!;

    GrpcEventReader Reader(IAccessTokenSource source) {
        var client = FakeKurrentDbHandler.Client(_handler, configure: s => _usage = GrpcAuthentication.Apply(s, source));
        var auth   = new GrpcAuthContext(source, CancellationToken.None, _time, "reader");

        // realtime subscription that succeeds immediately, so ReadEvents gets to the $all read
        return new GrpcEventReader(client, auth, cache => new Realtime((_, _, _, _) => Task.FromResult<IDisposable>(new MemoryStream()), cache, auth));
    }

    [Test]
    public async Task Read_all_carries_the_token_and_a_rejection_invalidates_it() {
        await File.WriteAllTextAsync(_file, "A");
        var source       = new TokenFileSource(_file, TimeSpan.FromHours(1), _time, "reader");
        var reader       = Reader(source);
        _handler.Respond = _ => Task.FromResult(FakeKurrentDbHandler.TrailersOnly(StatusCode.Unauthenticated));

        await Assert.That(async () => await reader.ReadEvents(LogPosition.Start, _ => ValueTask.CompletedTask, default)).Throws<Exception>();
        await Assert.That(_handler.AuthorizationsFor(FakeKurrentDbHandler.ReadPath).Single()).IsEqualTo("Bearer A");

        await File.WriteAllTextAsync(_file, "B");
        _handler.Respond = _ => Task.FromResult(FakeKurrentDbHandler.TrailersOnly(StatusCode.Unavailable));
        await Assert.That(async () => await reader.ReadEvents(LogPosition.Start, _ => ValueTask.CompletedTask, default)).Throws<Exception>();
        await Assert.That(_handler.AuthorizationsFor(FakeKurrentDbHandler.ReadPath).Last()).IsEqualTo("Bearer B"); // re-read immediately after rejection

        // EventStoreClient discovers server features lazily, once per distinct gRPC service/operation it uses on
        // this client instance (see GrpcAuthenticationTests), not once per client overall: each of the two
        // ReadEvents calls here triggers its own one-time discovery RPC through the fallback path (controller
        // ruling, implementer-contract.md). Assert the observed count, keeping the per-RPC header assertions above.
        await Assert.That(_usage.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetLastPosition_retries_a_rejected_token_with_the_new_one() {
        await File.WriteAllTextAsync(_file, "A");
        var source = new TokenFileSource(_file, TimeSpan.FromHours(1), _time, "reader");
        var reader = Reader(source);
        _handler.Respond = s => {
            if (s.Authorization == "Bearer A") File.WriteAllText(_file, "B"); // rotate while rejecting A

            return Task.FromResult(FakeKurrentDbHandler.TrailersOnly(s.Authorization == "Bearer A" ? StatusCode.Unauthenticated : StatusCode.Unavailable));
        };

        var call = reader.GetLastPosition(default);
        await Assert.That(async () => await TimeDriver.Drive(call, _time)).Throws<Exception>(); // Unavailable with B is not a token error
        await Assert.That(_handler.AuthorizationsFor(FakeKurrentDbHandler.ReadPath)).IsEquivalentTo(new[] { "Bearer A", "Bearer B" });

        // See the comment in Read_all_carries_the_token_and_a_rejection_invalidates_it: discovery-driven fallback
        // uses are per distinct gRPC call attempt on this client, not per logical operation.
        await Assert.That(_usage.Count).IsEqualTo(2);
    }
}
