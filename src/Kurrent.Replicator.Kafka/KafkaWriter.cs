using Kurrent.Replicator.Shared.Contracts;
using Kurrent.Replicator.Shared.Logging;
using Kurrent.Replicator.Shared.Observe;
using Ubiquitous.Metrics;

namespace Kurrent.Replicator.Kafka;

public class KafkaWriter : IEventWriter {
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();

    readonly IProducer<string, byte[]>         _producer;
    readonly Action<string, object[]>?         _debug;
    readonly Func<ProposedEvent, MessageRoute> _route;

    KafkaWriter(ProducerConfig config) {
        var producerBuilder = new ProducerBuilder<string, byte[]>(config);
        _producer = producerBuilder.Build();
        _debug    = Log.IsDebugEnabled() ? Log.Debug : null;
        _route    = x => DefaultRouters.RouteByCategory(x.EventDetails.Stream);
    }

    public KafkaWriter(ProducerConfig config, string? routingFunction) : this(config) {
        if (routingFunction != null)
            _route = new KafkaJsMessageRouter(routingFunction).Route;
    }

    public Task Start() => Task.CompletedTask;

    public Task<long> WriteEvent(BaseProposedEvent proposedEvent, CancellationToken cancellationToken) {
        var task = proposedEvent switch {
            ProposedEvent p      => Append(p),
            ProposedMetaEvent    => NoOp(),
            ProposedDeleteStream => NoOp(),
            IgnoredEvent         => NoOp(),
            _                    => throw new InvalidOperationException("Unknown proposed event type")
        };

        return Metrics.Measure(() => task, ReplicationMetrics.WritesHistogram, ReplicationMetrics.WriteErrorsCount);

        async Task<long> Append(ProposedEvent p) {
            var (topic, partitionKey) = _route(p);

            _debug?.Invoke(
                "Kafka: Write event with id {Id} of type {Type} to {Stream} with original position {Position}",
                [
                    proposedEvent.EventDetails.EventId,
                    proposedEvent.EventDetails.EventType,
                    topic,
                    proposedEvent.SourceLogPosition.EventPosition
                ]
            );

            // TODO: Map meta to headers, but only for JSON
            var message = new Message<string, byte[]> {
                Key   = partitionKey,
                Value = p.Data
            };

            var result = await _producer.ProduceAsync(topic, message, cancellationToken).ConfigureAwait(false);

            return result.Offset.Value;
        }

        static Task<long> NoOp() => Task.FromResult(-1L);
    }
}
