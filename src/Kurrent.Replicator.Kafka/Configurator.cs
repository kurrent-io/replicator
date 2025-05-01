using Kurrent.Replicator.Js;

namespace Kurrent.Replicator.Kafka;

public class KafkaConfigurator(string router) : IConfigurator {
    public string Protocol => "kafka";

    public IEventReader ConfigureReader(string connectionString) {
        throw new NotImplementedException("Kafka reader is not supported");
    }

    public IEventWriter ConfigureWriter(string connectionString)
        => new KafkaWriter(ParseKafkaConnection(connectionString), FunctionLoader.LoadFile(router, "Router"));

    static ProducerConfig ParseKafkaConnection(string connectionString) {
        var settings = connectionString.Split(';');
        var dict     = settings.Select(ParsePair).ToDictionary(x => x.Key, x => x.Value);

        return new(dict);
    }

    static KeyValuePair<string, string> ParsePair(string s) {
        var split = s.Split('=');

        return new(split[0].Trim(), split[1].Trim());
    }
}
