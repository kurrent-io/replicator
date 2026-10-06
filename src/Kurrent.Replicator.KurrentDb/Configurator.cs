using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Shared.Logging;

namespace Kurrent.Replicator.KurrentDb;

public class GrpcConfigurator : IConfigurator {
    static ILog Log => LogProvider.GetLogger(typeof(GrpcConfigurator));

    readonly GrpcAuthOptions                   _readerAuth;
    readonly GrpcAuthOptions                   _sinkAuth;
    readonly CancellationToken                 _shutdown;
    readonly Action<EventStoreClientSettings>? _configureSettings;
    readonly TimeProvider                      _time;

    public GrpcConfigurator() : this(GrpcAuthOptions.Default, GrpcAuthOptions.Default, CancellationToken.None) { }

    public GrpcConfigurator(GrpcAuthOptions readerAuth, GrpcAuthOptions sinkAuth, CancellationToken shutdown)
        : this(readerAuth, sinkAuth, shutdown, null, TimeProvider.System) { }

    internal GrpcConfigurator(
            GrpcAuthOptions                   readerAuth,
            GrpcAuthOptions                   sinkAuth,
            CancellationToken                 shutdown,
            Action<EventStoreClientSettings>? configureSettings,
            TimeProvider                      time
        ) {
        _readerAuth        = readerAuth;
        _sinkAuth          = sinkAuth;
        _shutdown          = shutdown;
        _configureSettings = configureSettings;
        _time              = time;
    }

    public string Protocol => "grpc";

    public IEventReader ConfigureReader(string connectionString) {
        var (client, auth) = Configure(connectionString, _readerAuth, "reader", follower: true);

        return new GrpcEventReader(client, auth);
    }

    public IEventWriter ConfigureWriter(string connectionString) {
        var (client, auth) = Configure(connectionString, _sinkAuth, "sink", follower: false);

        return new GrpcEventWriter(client, auth);
    }

    (EventStoreClient Client, GrpcAuthContext Auth) Configure(string connectionString, GrpcAuthOptions options, string side, bool follower) {
        var settings = EventStoreClientSettings.Create(connectionString);

        if (follower) settings.ConnectivitySettings.NodePreference = NodePreference.Follower;

        _configureSettings?.Invoke(settings);

        GrpcAuthOptionsValidator.EnsureValid(side, options, settings);

        foreach (var warning in GrpcAuthOptionsValidator.Warnings(options)) Log.Warn("{Side}: {Warning}", side, warning);

        var source = GrpcAuthentication.CreateSource(options, side, _time, _shutdown);

        if (source != null) {
            GrpcAuthentication.Apply(settings, source);
            Log.Info("{Side}: using {AuthType} authentication", side, options.Type);
        }

        return (new EventStoreClient(settings), new GrpcAuthContext(source, _shutdown, _time, side));
    }
}
