#nullable enable
using EventStore.Client;
using Kurrent.Replicator.KurrentDb.Auth;
using TUnit.Assertions.AssertConditions.Throws;

namespace Kurrent.Replicator.Tests.Auth;

public class GrpcAuthOptionsValidatorTests {
    static readonly EventStoreClientSettings Tls = EventStoreClientSettings.Create("esdb://localhost:2113?tls=true");

    static GrpcAuthOptions Cc(Func<GrpcAuthOptions, GrpcAuthOptions>? f = null) {
        var o = new GrpcAuthOptions { Type = GrpcAuthType.OAuthClientCredentials, TokenEndpoint = "https://idp/t", ClientId = "c", ClientSecret = "s" };

        return f?.Invoke(o) ?? o;
    }

    [Test]
    public async Task Connection_string_mode_is_always_valid() {
        var basic = EventStoreClientSettings.Create("esdb://admin:changeit@localhost:2113?tls=false");
        await Assert.That(GrpcAuthOptionsValidator.Validate(GrpcAuthOptions.Default, basic)).IsEmpty();
    }

    [Test]
    public async Task Valid_client_credentials_pass() => await Assert.That(GrpcAuthOptionsValidator.Validate(Cc(), Tls)).IsEmpty();

    [Test]
    [Arguments("esdb://admin:changeit@localhost:2113?tls=true", "credentials")]
    [Arguments("esdb://localhost:2113?tls=false", "tls")]
    public async Task Connection_string_conflicts_are_errors(string cs, string expected) {
        var errors = GrpcAuthOptionsValidator.Validate(Cc(), EventStoreClientSettings.Create(cs));
        await Assert.That(string.Join(";", errors)).Contains(expected);
    }

    [Test]
    public async Task Client_credentials_rules() {
        async Task Expect(GrpcAuthOptions o, string fragment)
            => await Assert.That(string.Join(";", GrpcAuthOptionsValidator.Validate(o, Tls))).Contains(fragment);

        await Expect(Cc(o => o with { TokenEndpoint = null }), "tokenEndpoint is required");
        await Expect(Cc(o => o with { TokenEndpoint = "http://idp.example.com/t" }), "https");
        await Expect(Cc(o => o with { TokenEndpoint = "/relative" }), "https");
        await Expect(Cc(o => o with { ClientId = " " }), "clientId is required");
        await Expect(Cc(o => o with { ClientSecret = null }), "exactly one of");
        await Expect(Cc(o => o with { ClientSecretFile = "/x" }), "exactly one of");
        await Expect(Cc(o => o with { ClientSecret = null, ClientSecretFile = "/does/not/exist" }), "clientSecretFile");
        await Expect(Cc(o => o with { ClientSecret = null, ClientAssertionFile = "/does/not/exist" }), "clientAssertionFile");
        await Expect(Cc(o => o with { RefreshBeforeExpirySeconds = -1 }), "refreshBeforeExpirySeconds");
        await Expect(Cc(o => o with { DefaultTokenLifetimeSeconds = 0 }), "defaultTokenLifetimeSeconds");
        await Expect(Cc(o => o with { AdditionalParameters = new Dictionary<string, string> { ["Scope"] = "x", ["audience"] = "y" } }), "Scope");
    }

    [Test]
    public async Task Loopback_http_is_allowed() {
        await Assert.That(GrpcAuthOptionsValidator.Validate(Cc(o => o with { TokenEndpoint = "http://127.0.0.1:8080/t" }), Tls)).IsEmpty();
        await Assert.That(GrpcAuthOptionsValidator.Validate(Cc(o => o with { TokenEndpoint = "http://localhost:8080/t" }), Tls)).IsEmpty();
    }

    [Test]
    public async Task Empty_secret_file_is_an_error() {
        var file = Path.GetTempFileName();
        await File.WriteAllTextAsync(file, "  \n");
        var errors = GrpcAuthOptionsValidator.Validate(Cc(o => o with { ClientSecret = null, ClientSecretFile = file }), Tls);
        await Assert.That(string.Join(";", errors)).Contains("clientSecretFile");
        File.Delete(file);
    }

    [Test]
    public async Task Token_file_rules() {
        var ok = new GrpcAuthOptions { Type = GrpcAuthType.OAuthTokenFile, TokenFile = "/not/yet/written" };
        await Assert.That(GrpcAuthOptionsValidator.Validate(ok, Tls)).IsEmpty(); // existence not required at startup
        await Assert.That(string.Join(";", GrpcAuthOptionsValidator.Validate(ok with { TokenFile = null }, Tls))).Contains("tokenFile is required");
        await Assert.That(string.Join(";", GrpcAuthOptionsValidator.Validate(ok with { TokenFileReloadSeconds = 0 }, Tls))).Contains("tokenFileReloadSeconds");
    }

    [Test]
    public async Task EnsureValid_reports_all_errors_with_side() {
        var ex = await Assert.That(() => GrpcAuthOptionsValidator.EnsureValid("reader", Cc(o => o with { ClientId = null, TokenEndpoint = null }), Tls))
            .Throws<InvalidOperationException>();
        await Assert.That(ex!.Message).StartsWith("Invalid reader auth configuration: ");
        await Assert.That(ex.Message).Contains("clientId is required");
        await Assert.That(ex.Message).Contains("tokenEndpoint is required");
    }

    [Test]
    public async Task Protocol_must_be_grpc_for_oauth() {
        await Assert.That(GrpcAuthOptionsValidator.ValidateProtocol(Cc(), "tcp")).IsNotNull();
        await Assert.That(GrpcAuthOptionsValidator.ValidateProtocol(Cc(), "GRPC")).IsNull();
        await Assert.That(GrpcAuthOptionsValidator.ValidateProtocol(GrpcAuthOptions.Default, "tcp")).IsNull();
    }

    [Test]
    public async Task Oauth_options_with_connection_string_type_warn() {
        var warnings = GrpcAuthOptionsValidator.Warnings(GrpcAuthOptions.Default with { ClientId = "c", TokenFile = "/t" });
        await Assert.That(string.Join(";", warnings)).Contains("clientId");
        await Assert.That(string.Join(";", warnings)).Contains("tokenFile");
        await Assert.That(GrpcAuthOptionsValidator.Warnings(Cc())).IsEmpty();
    }
}
