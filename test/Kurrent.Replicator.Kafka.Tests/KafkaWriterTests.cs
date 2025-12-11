#nullable enable
using System.Reflection;
using System.Text.Json;
using Confluent.Kafka;
using Kurrent.Replicator.Shared.Observe;
using Kurrent.Replicator.Shared.Contracts;
using NUnit.Framework;
using Ubiquitous.Metrics;

namespace Kurrent.Replicator.Kafka.Tests;

[TestFixture]
public class KafkaWriterHeadersTests {
    [OneTimeSetUp]
    public void OneTimeSetup() {
        // Configure metrics to avoid NREs in Metrics.Measure during tests
        ReplicationMetrics.Configure(Metrics.CreateUsing());
    }

    [Test]
    public async Task Should_map_metadata_to_headers_for_json_events() {
        var writer = CreateWriterWithFakeProducer(out var producer);

        var metadataObj = new {
            s   = "hello",
            n   = 123,
            b   = true,
            obj = new { a = 1 },
            arr = new[] { 1, 2 }
        };

        var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadataObj);

        var proposedEvent = new ProposedEvent(
            new("category-stream", Guid.NewGuid(), "TestEvent", ContentTypes.Json),
            "data"u8.ToArray(),
            metadataBytes,
            new(0L, 0UL),
            0L,
            Guid.NewGuid()
        );

        var result = await writer.WriteEvent(proposedEvent, CancellationToken.None);

        Assert.That(result, Is.EqualTo(42));

        Assert.That(producer.LastTopic, Is.EqualTo("category"));
        Assert.That(producer.LastMessage, Is.Not.Null);

        var headers = producer.LastMessage!.Headers;
        Assert.That(headers, Is.Not.Null);
        Assert.That(headers!.Count, Is.EqualTo(5));

        var sHeader   = headers.First(h => h.Key == "s");
        var nHeader   = headers.First(h => h.Key == "n");
        var bHeader   = headers.First(h => h.Key == "b");
        var objHeader = headers.First(h => h.Key == "obj");
        var arrHeader = headers.First(h => h.Key == "arr");

        Assert.That(sHeader.GetValueBytes(), Is.EqualTo("hello"u8.ToArray()));
        Assert.That(nHeader.GetValueBytes(), Is.EqualTo("123"u8.ToArray()));
        Assert.That(bHeader.GetValueBytes(), Is.EqualTo("true"u8.ToArray()));
        Assert.That(objHeader.GetValueBytes(), Is.EqualTo("{\"a\":1}"u8.ToArray()));
        Assert.That(arrHeader.GetValueBytes(), Is.EqualTo("[1,2]"u8.ToArray()));
    }

    [Test]
    public async Task Should_not_set_headers_for_non_json_content_type() {
        var writer = CreateWriterWithFakeProducer(out var producer);

        var metadata = "{\"x\":1}"u8.ToArray();

        var proposedEvent = new ProposedEvent(
            new("stream-1", Guid.NewGuid(), "TestEvent", ContentTypes.Binary),
            "data"u8.ToArray(),
            metadata,
            new(0L, 0UL),
            0L,
            Guid.NewGuid()
        );

        await writer.WriteEvent(proposedEvent, CancellationToken.None);

        Assert.That(producer.LastMessage, Is.Not.Null);
        Assert.That(producer.LastMessage!.Headers, Is.Null);
    }

    [Test]
    public async Task Should_not_set_headers_when_metadata_is_null_or_empty() {
        var writer = CreateWriterWithFakeProducer(out var producer);

        var withNullMetadata = new ProposedEvent(
            new("stream-1", Guid.NewGuid(), "TestEvent", ContentTypes.Json),
            "data"u8.ToArray(),
            null,
            new(0L, 0UL),
            0L,
            Guid.NewGuid()
        );

        await writer.WriteEvent(withNullMetadata, CancellationToken.None);

        Assert.That(producer.LastMessage, Is.Not.Null);
        Assert.That(producer.LastMessage!.Headers, Is.Null);

        var withEmptyMetadata = new ProposedEvent(
            new("stream-1", Guid.NewGuid(), "TestEvent", ContentTypes.Json),
            "data"u8.ToArray(),
            Array.Empty<byte>(),
            new(0L, 0UL),
            0L,
            Guid.NewGuid()
        );

        await writer.WriteEvent(withEmptyMetadata, CancellationToken.None);

        Assert.That(producer.LastMessage, Is.Not.Null);
        Assert.That(producer.LastMessage!.Headers, Is.Null);
    }

    [Test]
    public async Task Should_not_set_headers_when_metadata_is_malformed_json() {
        var writer = CreateWriterWithFakeProducer(out var producer);

        var badMetadata = "{not json}"u8.ToArray();

        var proposedEvent = new ProposedEvent(
            new("stream-1", Guid.NewGuid(), "TestEvent", ContentTypes.Json),
            "data"u8.ToArray(),
            badMetadata,
            new(0L, 0UL),
            0L,
            Guid.NewGuid()
        );

        await writer.WriteEvent(proposedEvent, CancellationToken.None);

        Assert.That(producer.LastMessage, Is.Not.Null);
        Assert.That(producer.LastMessage!.Headers, Is.Null);
    }

    static KafkaWriter CreateWriterWithFakeProducer(out CapturingProducer producer) {
        var config = new ProducerConfig {
            BootstrapServers = "dummy:9092"
        };

        var writer = new KafkaWriter(config, null);

        var field = typeof(KafkaWriter).GetField("_producer", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null);

        producer = new();
        field!.SetValue(writer, producer);

        return writer;
    }

    class CapturingProducer : IProducer<string, byte[]> {
        public string? LastTopic { get; private set; }
        public Message<string, byte[]>? LastMessage { get; private set; }

        public string Name => "capturing-producer";

        public Handle Handle => throw new NotSupportedException();

        public Task<DeliveryResult<string, byte[]>> ProduceAsync(string topic, Message<string, byte[]> message, CancellationToken cancellationToken = default) {
            LastTopic  = topic;
            LastMessage = message;

            var result = new DeliveryResult<string, byte[]> {
                Topic     = topic,
                Message   = message,
                Offset    = new(42),
                Partition = new(0),
                Status    = PersistenceStatus.Persisted
            };

            return Task.FromResult(result);
        }

        public Task<DeliveryResult<string, byte[]>> ProduceAsync(TopicPartition topicPartition, Message<string, byte[]> message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Produce(string topic, Message<string, byte[]> message, Action<DeliveryReport<string, byte[]>>? deliveryHandler = null)
            => throw new NotSupportedException();

        public void Produce(TopicPartition topicPartition, Message<string, byte[]> message, Action<DeliveryReport<string, byte[]>>? deliveryHandler = null)
            => throw new NotSupportedException();

        public int Flush(TimeSpan timeout) => 0;

        public void Flush(CancellationToken cancellationToken) { }

        public int Poll(TimeSpan timeout) => 0;

        public int AddBrokers(string brokers) => 0;

        public Metadata GetMetadata(string topic, TimeSpan timeout) => throw new NotSupportedException();

        public Metadata GetMetadata(TimeSpan timeout) => throw new NotSupportedException();

        public void InitTransactions(TimeSpan timeout) => throw new NotSupportedException();

        public void BeginTransaction() => throw new NotSupportedException();

        public void CommitTransaction(TimeSpan timeout) => throw new NotSupportedException();

        public void CommitTransaction() => throw new NotSupportedException();

        public void AbortTransaction(TimeSpan timeout) => throw new NotSupportedException();

        public void AbortTransaction() => throw new NotSupportedException();

        public void SendOffsetsToTransaction(IEnumerable<TopicPartitionOffset> offsets, IConsumerGroupMetadata groupMetadata, TimeSpan timeout)
            => throw new NotSupportedException();

        public void SendOffsetsToTransaction(IEnumerable<TopicPartitionOffset> offsets, IConsumerGroupMetadata groupMetadata, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void SetSaslCredentials(string username, string password) => throw new NotSupportedException();

        public void Dispose() { }
    }
}
