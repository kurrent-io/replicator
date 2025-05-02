using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using EventStore.Client;
using EventStore.ClientAPI;
using Testcontainers.EventStoreDb;
using TUnit.Core.Interfaces;

namespace Kurrent.Replicator.Tests.Fixtures;

public class ContainerFixture : IAsyncInitializer, IAsyncDisposable {
    EventStoreDbContainer _kurrentDbContainer;
    IContainer            _eventStoreContainer;
    public DirectoryInfo  V5DataPath { get; private set; }

    public async Task InitializeAsync() {
        V5DataPath           = Directory.CreateTempSubdirectory();
        _kurrentDbContainer  = BuildV23Container();
        _eventStoreContainer = BuildV5Container(V5DataPath);

        await _kurrentDbContainer.StartAsync();
        await _eventStoreContainer.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(2)); // give it some time to spin up
    }

    public async ValueTask DisposeAsync() {
        await Task.WhenAll(_kurrentDbContainer.StopAsync(), _eventStoreContainer.StopAsync());
        await _eventStoreContainer.DisposeAsync();
        await _kurrentDbContainer.DisposeAsync();
    }

    public IEventStoreConnection GetV5Client() {
        var connectionString = $"ConnectTo=tcp://admin:changeit@localhost:{_eventStoreContainer.GetMappedPublicPort(1113)}; HeartBeatTimeout=500; UseSslConnection=false;";
        var client           = ConfigureEventStoreTcp(connectionString);

        return client;
    }

    public EventStoreClient GetKurrentClient() {
        var connectionString = _kurrentDbContainer.GetConnectionString();
        var settings         = EventStoreClientSettings.Create(connectionString);

        return new(settings);
    }

    static EventStoreDbContainer BuildV23Container() => new EventStoreDbBuilder()
        .WithImage("eventstore/eventstore:24.10")
        .WithEnvironment("EVENTSTORE_RUN_PROJECTIONS", "None")
        .WithEnvironment("EVENTSTORE_START_STANDARD_PROJECTIONS", "false")
        .WithEnvironment("EVENTSTORE_ENABLE_ATOM_PUB_OVER_HTTP", bool.TrueString)
        .Build();

    const int TcpPort  = 1113;
    const int HttpPort = 2113;
    
    static IContainer BuildV5Container(DirectoryInfo data) => new ContainerBuilder()
        .WithImage("eventstore/eventstore:5.0.11-bionic")
        .WithEnvironment("EVENTSTORE_CLUSTER_SIZE", "1")
        .WithEnvironment("EVENTSTORE_RUN_PROJECTIONS", "None")
        .WithEnvironment("EVENTSTORE_START_STANDARD_PROJECTIONS", "false")
        .WithEnvironment("EVENTSTORE_EXT_HTTP_PORT", "2113")
        .WithBindMount(data.FullName, "/var/lib/eventstore")
        .WithExposedPort(HttpPort)
        .WithExposedPort(TcpPort)
        .WithPortBinding(HttpPort, true)
        .WithPortBinding(TcpPort, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(TcpPort))
        .Build();

    static IEventStoreConnection ConfigureEventStoreTcp(string connectionString) {
        var builder = ConnectionSettings.Create()
            .KeepReconnecting()
            .KeepRetrying();

        var connection = EventStoreConnection.Create(connectionString, builder);

        return connection;
    }
}
