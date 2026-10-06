#nullable enable
using System.Text;
using System.Text.Json;
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.KurrentDb.Internals;
using Contracts = Kurrent.Replicator.Shared.Contracts;

namespace Kurrent.Replicator.Tests.Resilience;

/// <summary>
/// The sink writes stream metadata as a plain append of a <c>$metadata</c> event (so a retry can reuse the source
/// event id), which means the replicator produces the metadata JSON itself.
/// </summary>
public class MetadataWriteFormatTests {
    static readonly Contracts.StreamMetadata Full = new(
        10,
        TimeSpan.FromHours(1),
        5,
        TimeSpan.FromSeconds(30),
        new(["r1"], ["w1", "w2"], ["d"], ["mr"], ["mw"])
    );

    [Test]
    public async Task Metadata_json_uses_KurrentDB_system_metadata_format() {
        var json = Encoding.UTF8.GetString(GrpcEventWriter.SerializeMetadata(Full));

        await Assert.That(json).IsEqualTo(
            """{"$maxCount":10,"$maxAge":3600,"$tb":5,"$cacheControl":30,"$acl":{"$r":["r1"],"$w":["w1","w2"],"$d":["d"],"$mr":["mr"],"$mw":["mw"]}}"""
        );
    }

    [Test]
    public async Task Unset_metadata_fields_are_omitted() {
        var json = Encoding.UTF8.GetString(GrpcEventWriter.SerializeMetadata(new(10, null, null, null, null)));

        await Assert.That(json).IsEqualTo("""{"$maxCount":10}""");
    }

    [Test]
    public async Task Metadata_json_round_trips_through_the_reader_deserializer() {
        var read = JsonSerializer.Deserialize<global::EventStore.Client.StreamMetadata>(
            GrpcEventWriter.SerializeMetadata(Full),
            MetaSerialization.StreamMetadataJsonSerializerOptions
        );

        await Assert.That(read.MaxCount).IsEqualTo(10);
        await Assert.That(read.MaxAge).IsEqualTo(TimeSpan.FromHours(1));
        await Assert.That(read.TruncateBefore?.ToInt64()).IsEqualTo(5L);
        await Assert.That(read.CacheControl).IsEqualTo(TimeSpan.FromSeconds(30));
        await Assert.That(read.Acl!.ReadRoles).IsEquivalentTo(new[] { "r1" });
        await Assert.That(read.Acl.WriteRoles).IsEquivalentTo(new[] { "w1", "w2" });
        await Assert.That(read.Acl.DeleteRoles).IsEquivalentTo(new[] { "d" });
        await Assert.That(read.Acl.MetaReadRoles).IsEquivalentTo(new[] { "mr" });
        await Assert.That(read.Acl.MetaWriteRoles).IsEquivalentTo(new[] { "mw" });
    }
}
