namespace Kurrent.Replicator.EventStore;

public class TcpConfigurator(int pageSize) : IConfigurator {
    public string Protocol => "tcp";

    public IEventReader ConfigureReader(string connectionString)
        => new TcpEventReader(ConfigureEventStoreTcp(connectionString, true), pageSize);

    public IEventWriter ConfigureWriter(string connectionString)
        => new TcpEventWriter(ConfigureEventStoreTcp(connectionString, false));

    IEventStoreConnection ConfigureEventStoreTcp(string connectionString, bool follower) {
        var builder = ConnectionSettings.Create()
            .UseCustomLogger(new TcpClientLogger())
            .KeepReconnecting()
            .KeepRetrying();

        if (follower) {
            builder = builder.PreferFollowerNode();
        }

        var connection = EventStoreConnection.Create(connectionString, builder);

        return connection;
    }
}
