#nullable enable
using EventStore.Client;
using Grpc.Core;
using Kurrent.Replicator.KurrentDb;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.AssertConditions.Throws;

namespace Kurrent.Replicator.Tests.Auth;

/// <summary>
/// DEV-1903: KurrentDB 26 creates system streams (e.g. <c>$connectors-mngt/state-projection</c>) that have metadata with
/// a max count but no events. The scavenge filter's stream-size read for such a stream must not stall replication.
/// </summary>
public class ScavengedEventsFilterEmptyStreamTests {
    const string Stream = "$connectors-mngt/state-projection";

    readonly FakeTimeProvider        _time = new();
    readonly ControllableTokenSource _source;
    readonly GrpcAuthContext         _auth;

    public ScavengedEventsFilterEmptyStreamTests() {
        _source = new(_time) { Value = "A" };
        _auth   = new(_source, CancellationToken.None, _time, "reader");
    }

    static readonly StreamMeta MaxCountMeta = new(false, null, 10, 0);

    ScavengedEventsFilter FilterWithSizeReadAnswer(Func<HttpResponseMessage> answer, out FakeKurrentDbHandler handler) {
        handler = new FakeKurrentDbHandler { Respond = s => Task.FromResult(s.Path == FakeKurrentDbHandler.ReadPath ? answer() : FakeKurrentDbHandler.TrailersOnly(StatusCode.Unavailable)) };
        var client = FakeKurrentDbHandler.Client(handler);

        return new((_, _, _) => Task.FromResult(MaxCountMeta), ScavengedEventsFilter.SizeReader(client), new StreamMetaCache(true), _auth);
    }

    [Test]
    public async Task Stream_size_of_a_stream_that_is_not_found_is_empty() {
        var handler = new FakeKurrentDbHandler { Respond = _ => Task.FromResult(FakeKurrentDbHandler.ReadStreamNotFound(Stream)) };
        var client  = FakeKurrentDbHandler.Client(handler);

        var size = await client.GetStreamSize(Stream);

        await Assert.That(size).IsEqualTo(StreamSize.Empty);
    }

    [Test]
    public async Task Stream_size_of_a_read_that_returns_no_events_is_empty() {
        var handler = new FakeKurrentDbHandler { Respond = _ => Task.FromResult(FakeKurrentDbHandler.EmptyRead()) };
        var client  = FakeKurrentDbHandler.Client(handler);

        var size = await client.GetStreamSize(Stream);

        await Assert.That(size).IsEqualTo(StreamSize.Empty);
    }

    [Test]
    public async Task Metadata_event_for_a_stream_with_metadata_but_no_events_is_kept() {
        var filter = FilterWithSizeReadAnswer(() => FakeKurrentDbHandler.ReadStreamNotFound(Stream), out var handler);

        var keep = await filter.Filter(TestEvents.Original(Stream, 0)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(keep).IsTrue();
        await Assert.That(handler.AuthorizationsFor(FakeKurrentDbHandler.ReadPath).Single()).IsEqualTo("Bearer A");
    }

    [Test]
    public async Task Event_is_kept_when_the_size_read_returns_no_events() {
        var filter = FilterWithSizeReadAnswer(FakeKurrentDbHandler.EmptyRead, out _);

        await Assert.That(await filter.Filter(TestEvents.Original(Stream, 0)).AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
    }

    [Test]
    public async Task Events_beyond_max_count_are_still_dropped() {
        var filter = new ScavengedEventsFilter(
            (_, _, _) => Task.FromResult(MaxCountMeta),
            (_, _, _) => Task.FromResult(new StreamSize(100)),
            new StreamMetaCache(true),
            _auth
        );

        await Assert.That(await filter.Filter(TestEvents.Original("s", 5))).IsFalse();
        await Assert.That(await filter.Filter(TestEvents.Original("s", 95))).IsTrue();
    }

    [Test]
    public async Task Permission_denied_on_the_size_read_still_fails_closed() {
        var filter = FilterWithSizeReadAnswer(() => FakeKurrentDbHandler.TrailersOnly(StatusCode.PermissionDenied), out _);

        Exception? error = null;

        try {
            await filter.Filter(TestEvents.Original(Stream, 0)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        } catch (Exception e) {
            error = e;
        }

        await Assert.That(error).IsNotNull();
        await Assert.That(AuthFailure.IsPermissionDenied(error!)).IsTrue();
    }

    [Test]
    public async Task Access_denied_from_the_size_reader_is_not_treated_as_an_empty_stream() {
        var filter = new ScavengedEventsFilter(
            (_, _, _) => Task.FromResult(MaxCountMeta),
            (_, _, _) => throw new AccessDeniedException("x", new RpcException(new Status(StatusCode.PermissionDenied, "x"))),
            new StreamMetaCache(true),
            _auth
        );

        await Assert.That(async () => await filter.Filter(TestEvents.Original(Stream, 0))).Throws<AccessDeniedException>();
    }

    [Test]
    public async Task Stream_without_metadata_reads_as_no_scavenge_settings() {
        // GetStreamMetadataAsync answers a missing metastream with no MetastreamRevision; that is "no metadata", not an error
        var handler = new FakeKurrentDbHandler { Respond = _ => Task.FromResult(FakeKurrentDbHandler.ReadStreamNotFound("$$s")) };
        var client  = FakeKurrentDbHandler.Client(handler);

        var meta = await client.GetStreamMeta("s");

        await Assert.That(meta).IsEqualTo(new StreamMeta(false, null, null, -1));
    }
}
