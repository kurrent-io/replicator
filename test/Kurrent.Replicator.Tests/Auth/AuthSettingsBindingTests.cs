#nullable enable
using Kurrent.Replicator.KurrentDb.Auth;
using Microsoft.Extensions.Configuration;
using TUnit.Assertions.AssertConditions.Throws;

namespace Kurrent.Replicator.Tests.Auth;

public class AuthSettingsBindingTests {
    static GrpcAuthOptions Bind(Dictionary<string, string?> values, string side) {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var raw    = config.GetSection($"Replicator:{side}:Auth").Get<GrpcAuthSettings>() ?? new GrpcAuthSettings();

        return raw.ToOptions(side.ToLowerInvariant());
    }

    [Test]
    public async Task Missing_section_means_connection_string() {
        var options = Bind(new(), "Reader");
        await Assert.That(options.Type).IsEqualTo(GrpcAuthType.ConnectionString);
        await Assert.That(options.IsOAuth).IsFalse();
    }

    [Test]
    public async Task Reader_basic_and_sink_oauth_bind_independently() {
        var values = new Dictionary<string, string?> {
            ["Replicator:Reader:ConnectionString"]          = "esdb://admin:changeit@source:2113",
            ["Replicator:Sink:Auth:Type"]                   = "OAuthClientCredentials",
            ["Replicator:Sink:Auth:TokenEndpoint"]          = "https://login.microsoftonline.com/t/oauth2/v2.0/token",
            ["Replicator:Sink:Auth:ClientId"]               = "app",
            ["Replicator:Sink:Auth:ClientSecretFile"]       = "/secrets/sink",
            ["Replicator:Sink:Auth:ClientAuthentication"]   = "Basic",
            ["Replicator:Sink:Auth:Scope"]                  = "api://kdb/.default",
            ["Replicator:Sink:Auth:RefreshBeforeExpirySeconds"] = "120"
        };

        var reader = Bind(values, "Reader");
        var sink   = Bind(values, "Sink");

        await Assert.That(reader.Type).IsEqualTo(GrpcAuthType.ConnectionString);
        await Assert.That(sink.Type).IsEqualTo(GrpcAuthType.OAuthClientCredentials);
        await Assert.That(sink.ClientAuthentication).IsEqualTo(ClientAuthenticationMethod.Basic);
        await Assert.That(sink.ClientSecretFile).IsEqualTo("/secrets/sink");
        await Assert.That(sink.RefreshBeforeExpirySeconds).IsEqualTo(120);
        await Assert.That(sink.TokenFileReloadSeconds).IsEqualTo(30);
    }

    [Test]
    public async Task Reader_oauth_and_sink_basic_bind_independently() {
        var values = new Dictionary<string, string?> {
            ["Replicator:Reader:Auth:Type"]      = "oauthTokenFile",
            ["Replicator:Reader:Auth:TokenFile"] = "/var/run/token",
            ["Replicator:Sink:Auth:Type"]        = "connectionString"
        };

        await Assert.That(Bind(values, "Reader").TokenFile).IsEqualTo("/var/run/token");
        await Assert.That(Bind(values, "Sink").IsOAuth).IsFalse();
    }

    [Test]
    public async Task Env_vars_bind_auth_including_additional_parameters() {
        // EnvConfigProvider turns REPLICATOR_A_B into the key REPLICATOR:A:B (case preserved, lookups case-insensitive)
        var env = new Dictionary<string, string?> {
            ["REPLICATOR_SINK_AUTH_TYPE"]                          = "oauthClientCredentials",
            ["REPLICATOR_SINK_AUTH_TOKENENDPOINT"]                 = "https://idp/t",
            ["REPLICATOR_SINK_AUTH_CLIENTID"]                      = "c",
            ["REPLICATOR_SINK_AUTH_CLIENTSECRET"]                  = "s",
            ["REPLICATOR_SINK_AUTH_ADDITIONALPARAMETERS_AUDIENCE"] = "kurrentdb"
        };
        var values = env.ToDictionary(kv => kv.Key.Replace("_", ":"), kv => kv.Value);

        var sink = Bind(values, "Sink");
        await Assert.That(sink.Type).IsEqualTo(GrpcAuthType.OAuthClientCredentials);
        await Assert.That(sink.ClientSecret).IsEqualTo("s");
        await Assert.That(sink.AdditionalParameters["audience"]).IsEqualTo("kurrentdb");
    }

    [Test]
    public async Task Unknown_values_fail_with_the_side() {
        var badType = new Dictionary<string, string?> { ["Replicator:Reader:Auth:Type"] = "oauth" };
        var ex      = await Assert.That(() => { Bind(badType, "Reader"); }).Throws<InvalidOperationException>();
        await Assert.That(ex!.Message).StartsWith("Invalid reader auth configuration");

        var badMethod = new Dictionary<string, string?> {
            ["Replicator:Sink:Auth:Type"] = "oauthClientCredentials", ["Replicator:Sink:Auth:ClientAuthentication"] = "jwt"
        };
        await Assert.That(() => { Bind(badMethod, "Sink"); }).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ToString_never_prints_secrets() {
        var raw = new GrpcAuthSettings { Type = "oauthClientCredentials", ClientSecret = "s3cr3t" };
        await Assert.That(raw.ToString()).DoesNotContain("s3cr3t");
        await Assert.That(raw.ToOptions("sink").ToString()).DoesNotContain("s3cr3t");
    }
}
