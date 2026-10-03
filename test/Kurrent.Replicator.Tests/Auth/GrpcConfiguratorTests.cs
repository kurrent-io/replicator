using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Shared.Observe;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.AssertConditions.Throws;
using Ubiquitous.Metrics;
using Ubiquitous.Metrics.NoMetrics;

namespace Kurrent.Replicator.Tests.Auth;

public class GrpcConfiguratorTests {
    readonly string _file = Path.Combine(Path.GetTempPath(), $"replicator-cfg-{Guid.NewGuid():N}");

    public GrpcConfiguratorTests() => ReplicationMetrics.Configure(Metrics.CreateUsing(new NoMetricsProvider()));

    [After(Test)]
    public void Cleanup() {
        if (File.Exists(_file)) File.Delete(_file);
    }

    [Test]
    public async Task Reader_basic_and_sink_oauth_send_independent_headers() {
        await File.WriteAllTextAsync(_file, "SINK-TOKEN");
        var handler  = new FakeKurrentDbHandler { Respond = s => Task.FromResult(s.Path == FakeKurrentDbHandler.AppendPath ? FakeKurrentDbHandler.AppendSuccess() : FakeKurrentDbHandler.TrailersOnly(Grpc.Core.StatusCode.Unavailable)) };
        var sinkAuth = new GrpcAuthOptions { Type = GrpcAuthType.OAuthTokenFile, TokenFile = _file };

        var configurator = new GrpcConfigurator(GrpcAuthOptions.Default, sinkAuth, CancellationToken.None, s => s.CreateHttpMessageHandler = () => handler, new FakeTimeProvider());

        var writer = configurator.ConfigureWriter("esdb://target.example.com:2113?tls=true");
        var reader = configurator.ConfigureReader("esdb://admin:changeit@target.example.com:2113?tls=true");

        await writer.WriteEvent(TestEvents.Proposed("s"), default);
        await Assert.That(async () => await reader.GetLastPosition(default)).Throws<Exception>();

        await Assert.That(handler.AuthorizationsFor(FakeKurrentDbHandler.AppendPath).Single()).IsEqualTo("Bearer SINK-TOKEN");
        await Assert.That(handler.AuthorizationsFor(FakeKurrentDbHandler.ReadPath).Single()).StartsWith("Basic ");
    }

    [Test]
    public async Task Invalid_side_configuration_names_the_side() {
        var oauth        = new GrpcAuthOptions { Type = GrpcAuthType.OAuthTokenFile, TokenFile = _file };
        var configurator = new GrpcConfigurator(oauth, GrpcAuthOptions.Default, CancellationToken.None);

        var ex = await Assert.That(() => configurator.ConfigureReader("esdb://admin:changeit@localhost:2113?tls=true")).Throws<InvalidOperationException>();
        await Assert.That(ex!.Message).StartsWith("Invalid reader auth configuration");

        await Assert.That(() => configurator.ConfigureWriter("esdb://admin:changeit@localhost:2113?tls=false")).ThrowsNothing(); // sink is basic
    }
}
