using DockerComposeFixture;
using EventStore.Client;
using EventStore.ClientAPI;
using Kurrent.Replicator.Shared.Observe;
using Kurrent.Replicator.Tests.Fakes;
using Ubiquitous.Metrics;
using Ubiquitous.Metrics.NoMetrics;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Kurrent.Replicator.Tests; 

public class Fixture : DockerFixture, IAsyncLifetime {
    const string TcpConnectionString =
        "ConnectTo=tcp://admin:changeit@localhost:1113; HeartBeatTimeout=500; UseSslConnection=false;";
    const string GrpcConnectionString = "esdb://localhost:2114?tls=false";

    public Fixture(IMessageSink output) : base(output) {
        output.OnMessage(new DiagnosticMessage("Starting containers..."));
        InitOnce(
            () => new DockerFixtureOptions {
                DockerComposeFiles = new[] {"docker-compose.yml"},
                CustomUpTest       = o => o.Any(l => l.Contains("IS MASTER.."))
            }
        );
        output.OnMessage(new DiagnosticMessage("Containers started"));

        TcpClient  = ConfigureEventStoreTcp(TcpConnectionString);
        GrpcClient = ConfigureEventStoreGrpc(GrpcConnectionString);
            
        ReplicationMetrics.Configure(Metrics.CreateUsing(new NoMetricsProvider()));
    }

    public IEventStoreConnection TcpClient       { get; }
    public EventStoreClient      GrpcClient      { get; }
    public CheckpointStore       CheckpointStore { get; } = new();

    public async Task InitializeAsync() {
        await TcpClient.ConnectAsync();
    }

    public Task DisposeAsync() {
        TcpClient.Close();
        return Task.CompletedTask;
    }

    static IEventStoreConnection ConfigureEventStoreTcp(string connectionString) {
        var builder = ConnectionSettings.Create()
            .KeepReconnecting()
            .KeepRetrying();

        var connection = EventStoreConnection.Create(connectionString, builder);

        return connection;
    }

    static EventStoreClient ConfigureEventStoreGrpc(string connectionString) {
        var settings = EventStoreClientSettings.Create(connectionString);
        return new EventStoreClient(settings);
    }
}