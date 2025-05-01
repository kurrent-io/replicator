namespace Kurrent.Replicator.KurrentDb; 

public class GrpcConfigurator : IConfigurator {
    public string Protocol => "grpc";

    public IEventReader ConfigureReader(string connectionString)
        => new GrpcEventReader(ConfigureEventStoreGrpc(connectionString, true));

    public IEventWriter ConfigureWriter(string connectionString)
        => new GrpcEventWriter(ConfigureEventStoreGrpc(connectionString, false));

    static EventStoreClient ConfigureEventStoreGrpc(string connectionString, bool follower) {
        var settings = EventStoreClientSettings.Create(connectionString);

        if (follower) {
            settings.ConnectivitySettings.NodePreference = NodePreference.Follower;
        }

        return new(settings);
    }
}