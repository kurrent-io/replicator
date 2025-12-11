using System.Text.Json;
using Kurrent.Replicator.Shared.Contracts;
using NUnit.Framework;

namespace Kurrent.Replicator.Kafka.Tests;

[TestFixture]
public class KafkaHeadersBuilderMetadataHeaderTests {
    [Test]
    public void Should_map_top_level_json_properties_to_headers() {
        var metadataObj = new {
            s   = "hello",
            n   = 123,
            b   = true,
            obj = new { a = 1 },
            arr = new[] { 1, 2 }
        };

        var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadataObj);

        var headers = KafkaHeadersBuilder.BuildHeaders(ContentTypes.Json, metadataBytes);

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
    public void Should_ignore_when_content_type_not_json() {
        var metadata = "{\"x\":1}"u8.ToArray();
        var headers  = KafkaHeadersBuilder.BuildHeaders(ContentTypes.Binary, metadata);
        Assert.That(headers, Is.Null);
    }

    [Test]
    public void Should_ignore_when_metadata_is_null_or_empty() {
        var nullHeaders  = KafkaHeadersBuilder.BuildHeaders(ContentTypes.Json, null);
        var emptyHeaders = KafkaHeadersBuilder.BuildHeaders(ContentTypes.Json, Array.Empty<byte>());
        Assert.That(nullHeaders, Is.Null);
        Assert.That(emptyHeaders, Is.Null);
    }

    [Test]
    public void Should_ignore_when_malformed_json() {
        var bad     = "{not json}"u8.ToArray();
        var headers = KafkaHeadersBuilder.BuildHeaders(ContentTypes.Json, bad);
        Assert.That(headers, Is.Null);
    }

    [Test]
    public void Should_ignore_when_root_is_not_object() {
        var arr     = "[1,2,3]"u8.ToArray();
        var headers = KafkaHeadersBuilder.BuildHeaders(ContentTypes.Json, arr);
        Assert.That(headers, Is.Null);
    }

    [Test]
    public void Should_ignore_when_object_has_no_properties() {
        var emptyObj = "{}"u8.ToArray();
        var headers  = KafkaHeadersBuilder.BuildHeaders(ContentTypes.Json, emptyObj);
        Assert.That(headers, Is.Null);
    }
}
