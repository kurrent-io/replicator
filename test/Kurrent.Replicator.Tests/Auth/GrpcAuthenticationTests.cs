#nullable enable
using EventStore.Client;
using Grpc.Core;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Tests.Auth.Support;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.AssertConditions.Throws;

namespace Kurrent.Replicator.Tests.Auth;

public class GrpcAuthenticationTests {
    static EventData Event() => new(Uuid.NewUuid(), "T", "{}"u8.ToArray());

    [Test]
    public async Task Fake_handler_append_succeeds_with_basic_credentials() {
        // Sanity check of the hand-encoded AppendResp: if this fails, fix FakeKurrentDbHandler before anything else.
        var handler = new FakeKurrentDbHandler { Respond = _ => Task.FromResult(FakeKurrentDbHandler.AppendSuccess()) };
        var client  = FakeKurrentDbHandler.Client(handler, "esdb://admin:changeit@localhost:2113?tls=true");
        var result  = await client.AppendToStreamAsync("s", StreamState.Any, [Event()]);
        await Assert.That(result.LogPosition.CommitPosition).IsEqualTo(1UL);
        await Assert.That(handler.AuthorizationsFor(FakeKurrentDbHandler.AppendPath).Single()).StartsWith("Basic ");
    }

    [Test]
    public async Task Fallback_hook_fetches_from_source_when_no_per_call_credentials() {
        var source   = new ControllableTokenSource(new FakeTimeProvider()) { Value = "A" };
        var handler  = new FakeKurrentDbHandler { Respond = _ => Task.FromResult(FakeKurrentDbHandler.AppendSuccess()) };
        FallbackUsage usage = null!;
        var client   = FakeKurrentDbHandler.Client(handler, configure: s => usage = GrpcAuthentication.Apply(s, source));

        await client.AppendToStreamAsync("s", StreamState.Any, [Event()]);
        source.Value = "B";
        await client.AppendToStreamAsync("s", StreamState.Any, [Event()]);

        await Assert.That(handler.AuthorizationsFor(FakeKurrentDbHandler.AppendPath)).IsEquivalentTo(new[] { "Bearer A", "Bearer B" });

        // EventStoreClient performs a one-time server-feature discovery RPC on first use of a client instance; that
        // internal call also goes through GetAuthenticationHeaderValue with the sentinel credentials (it is not an
        // application call, so it never carries per-call UserCredentials), adding 1 to the two real Append calls.
        await Assert.That(usage.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Per_call_credentials_bypass_the_fallback() {
        var source  = new ControllableTokenSource(new FakeTimeProvider()) { Value = "A" };
        var handler = new FakeKurrentDbHandler { Respond = _ => Task.FromResult(FakeKurrentDbHandler.AppendSuccess()) };
        FallbackUsage usage = null!;
        var client  = FakeKurrentDbHandler.Client(handler, configure: s => usage = GrpcAuthentication.Apply(s, source));

        await client.AppendToStreamAsync("s", StreamState.Any, [Event()], userCredentials: new UserCredentials("X"));
        await client.AppendToStreamAsync("s", StreamState.Any, [Event()], userCredentials: new UserCredentials("u", "p"));

        var headers = handler.AuthorizationsFor(FakeKurrentDbHandler.AppendPath);
        await Assert.That(headers[0]).IsEqualTo("Bearer X");
        await Assert.That(headers[1]).StartsWith("Basic ");

        // The one-time server-feature discovery RPC (see note above) runs before the first application call and
        // uses the sentinel/fallback path regardless of the explicit per-call credentials supplied afterwards.
        await Assert.That(usage.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Fallback_applies_to_streaming_reads() {
        var source  = new ControllableTokenSource(new FakeTimeProvider()) { Value = "A" };
        var handler = new FakeKurrentDbHandler();
        var client  = FakeKurrentDbHandler.Client(handler, configure: s => GrpcAuthentication.Apply(s, source));

        await Assert.That(async () => await client.ReadAllAsync(Direction.Forwards, Position.Start, 1).ToArrayAsync()).Throws<Exception>();
        await Assert.That(handler.AuthorizationsFor(FakeKurrentDbHandler.ReadPath).First()).IsEqualTo("Bearer A");
    }

    [Test]
    public async Task CreateSource_matches_the_type() {
        var time = TimeProvider.System;
        await Assert.That(GrpcAuthentication.CreateSource(GrpcAuthOptions.Default, "sink", time, default)).IsNull();
        await Assert.That(GrpcAuthentication.CreateSource(new() { Type = GrpcAuthType.OAuthTokenFile, TokenFile = "/t" }, "sink", time, default)).IsTypeOf<TokenFileSource>();

        var cc = new GrpcAuthOptions { Type = GrpcAuthType.OAuthClientCredentials, TokenEndpoint = "https://idp/t", ClientId = "c", ClientSecret = "s" };
        await Assert.That(GrpcAuthentication.CreateSource(cc, "sink", time, default)).IsTypeOf<ClientCredentialsTokenSource>();
    }
}
