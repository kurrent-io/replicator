using Kurrent.Replicator.Shared;

namespace Kurrent.Replicator; 

public class Factory(IEnumerable<IConfigurator> configurators) {
    readonly List<IConfigurator> _configurators = configurators.ToList();

    public IEventReader GetReader(string protocol, string connectionString)
        => GetConfigurator(protocol).ConfigureReader(connectionString);

    public IEventWriter GetWriter(string protocol, string connectionString)
        => GetConfigurator(protocol).ConfigureWriter(connectionString);

    IConfigurator GetConfigurator(string protocol) {
        var configurator = _configurators.FirstOrDefault(x => x.Protocol == protocol);

        return configurator ?? throw new NotSupportedException($"Unsupported protocol: {protocol}");
    }
}