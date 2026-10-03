#nullable enable
using System.Net;
using System.Text;
using Kurrent.Replicator.KurrentDb.Auth;
using Kurrent.Replicator.Tests.Auth.Support;
using TUnit.Assertions.AssertConditions.Throws;

namespace Kurrent.Replicator.Tests.Auth;

public class TokenEndpointClientTests {
    readonly StubTokenEndpoint _stub = new();

    static GrpcAuthOptions Options(Func<GrpcAuthOptions, GrpcAuthOptions>? configure = null) {
        var o = new GrpcAuthOptions {
            Type          = GrpcAuthType.OAuthClientCredentials,
            TokenEndpoint = "https://idp.example.com/oauth2/token",
            ClientId      = "replicator",
            ClientSecret  = "s3cr3t",
            Scope         = "api://kurrentdb/.default"
        };

        return configure?.Invoke(o) ?? o;
    }

    TokenEndpointClient Client(GrpcAuthOptions o) => new(o, _stub, "sink");

    [Test]
    public async Task Post_auth_sends_form_with_secret() {
        var o = Options(x => x with { AdditionalParameters = new Dictionary<string, string> { ["audience"] = "kdb" } });
        var r = await Client(o).RequestToken(default);

        await Assert.That(r.AccessToken).IsEqualTo("A");
        await Assert.That(r.Lifetime).IsEqualTo(TimeSpan.FromSeconds(3600));
        var (req, body) = _stub.Requests.Single();
        var form        = StubTokenEndpoint.Form(body);
        await Assert.That(req.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(req.Content!.Headers.ContentType!.MediaType).IsEqualTo("application/x-www-form-urlencoded");
        await Assert.That(form["grant_type"]).IsEqualTo("client_credentials");
        await Assert.That(form["client_id"]).IsEqualTo("replicator");
        await Assert.That(form["client_secret"]).IsEqualTo("s3cr3t");
        await Assert.That(form["scope"]).IsEqualTo("api://kurrentdb/.default");
        await Assert.That(form["audience"]).IsEqualTo("kdb");
        await Assert.That(req.Headers.Authorization).IsNull();
    }

    [Test]
    public async Task Post_auth_encodes_reserved_characters() {
        await Client(Options(x => x with { ClientSecret = "a+b/c=d%e f&g" })).RequestToken(default);
        var form = StubTokenEndpoint.Form(_stub.Requests.Single().Body);
        await Assert.That(form["client_secret"]).IsEqualTo("a+b/c=d%e f&g");
    }

    [Test]
    public async Task Basic_auth_form_encodes_reserved_characters() {
        await Client(Options(x => x with { ClientAuthentication = ClientAuthenticationMethod.Basic, ClientId = "my app", ClientSecret = "a+b:c" })).RequestToken(default);
        var (req, body) = _stub.Requests.Single();
        var form        = StubTokenEndpoint.Form(body);
        await Assert.That(form.ContainsKey("client_secret")).IsFalse();
        await Assert.That(form.ContainsKey("client_id")).IsFalse();
        await Assert.That(req.Headers.Authorization!.Scheme).IsEqualTo("Basic");
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(req.Headers.Authorization.Parameter!));
        await Assert.That(decoded).IsEqualTo("my+app:a%2Bb%3Ac"); // RFC 6749 2.3.1: form-urlencode each part
    }

    [Test]
    public async Task Assertion_is_reread_on_every_request() {
        var file = Path.GetTempFileName();
        await File.WriteAllTextAsync(file, "jwt-1\n");
        var client = Client(Options(x => x with { ClientSecret = null, ClientAssertionFile = file }));
        await client.RequestToken(default);
        await File.WriteAllTextAsync(file, "jwt-2");
        await client.RequestToken(default);

        var forms = _stub.Requests.Select(r => StubTokenEndpoint.Form(r.Body)).ToList();
        await Assert.That(forms[0]["client_assertion"]).IsEqualTo("jwt-1");
        await Assert.That(forms[1]["client_assertion"]).IsEqualTo("jwt-2");
        await Assert.That(forms[0]["client_assertion_type"]).IsEqualTo("urn:ietf:params:oauth:client-assertion-type:jwt-bearer");
        await Assert.That(forms[0]["client_id"]).IsEqualTo("replicator");
        await Assert.That(forms[0].ContainsKey("client_secret")).IsFalse();
        File.Delete(file);
    }

    [Test]
    public async Task Secret_file_is_read_once_and_trimmed() {
        var file = Path.GetTempFileName();
        await File.WriteAllTextAsync(file, "from-file\n");
        var client = Client(Options(x => x with { ClientSecret = null, ClientSecretFile = file }));
        await File.WriteAllTextAsync(file, "changed");
        await client.RequestToken(default);
        await Assert.That(StubTokenEndpoint.Form(_stub.Requests.Single().Body)["client_secret"]).IsEqualTo("from-file");
        File.Delete(file);
    }

    [Test]
    [Arguments("{\"token_type\":\"Bearer\",\"expires_in\":3600}")]                         // no access_token
    [Arguments("{\"access_token\":\"\",\"token_type\":\"Bearer\",\"expires_in\":3600}")]   // empty
    [Arguments("{\"access_token\":\"A\",\"expires_in\":3600}")]                            // no token_type
    [Arguments("{\"access_token\":\"A\",\"token_type\":\"mac\",\"expires_in\":3600}")]     // wrong type
    [Arguments("{\"access_token\":\"A\",\"token_type\":\"Bearer\",\"expires_in\":0}")]
    [Arguments("{\"access_token\":\"A\",\"token_type\":\"Bearer\",\"expires_in\":-5}")]
    [Arguments("{\"access_token\":\"A\",\"token_type\":\"Bearer\",\"expires_in\":1.5}")]
    [Arguments("{\"access_token\":\"A\",\"token_type\":\"Bearer\",\"expires_in\":\"soon\"}")]
    [Arguments("{\"access_token\":\"A\",\"token_type\":\"Bearer\",\"expires_in\":99999999999999999999}")]
    [Arguments("{\"access_token\":\"A\",\"token_type\":\"Bearer\"}")]                      // no expires_in, no default
    [Arguments("not json")]
    public async Task Invalid_responses_are_rejected(string json) {
        _stub.Respond = (_, _) => StubTokenEndpoint.Json(HttpStatusCode.OK, json);
        await Assert.That(async () => await Client(Options()).RequestToken(default)).Throws<OAuthTokenException>();
    }

    [Test]
    public async Task Numeric_string_expires_in_and_case_insensitive_bearer_are_accepted() {
        _stub.Respond = (_, _) => StubTokenEndpoint.Json(HttpStatusCode.OK, StubTokenEndpoint.Token("A", "120", "bearer"));
        var r = await Client(Options()).RequestToken(default);
        await Assert.That(r.Lifetime).IsEqualTo(TimeSpan.FromSeconds(120));
    }

    [Test]
    public async Task Missing_expires_in_uses_configured_default() {
        _stub.Respond = (_, _) => StubTokenEndpoint.Json(HttpStatusCode.OK, StubTokenEndpoint.Token("A", expiresIn: null));
        var r = await Client(Options(x => x with { DefaultTokenLifetimeSeconds = 900 })).RequestToken(default);
        await Assert.That(r.Lifetime).IsEqualTo(TimeSpan.FromSeconds(900));
    }

    [Test]
    public async Task Lifetime_over_24h_is_clamped() {
        _stub.Respond = (_, _) => StubTokenEndpoint.Json(HttpStatusCode.OK, StubTokenEndpoint.Token("A", 200000));
        var r = await Client(Options()).RequestToken(default);
        await Assert.That(r.Lifetime).IsEqualTo(TimeSpan.FromSeconds(86400));
    }

    [Test]
    public async Task Redirect_is_an_error_and_not_followed() {
        _stub.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://evil.example.com/steal") } };
        var ex = await Assert.That(async () => await Client(Options()).RequestToken(default)).Throws<OAuthTokenException>();
        await Assert.That(ex!.Message).Contains("redirect");
        await Assert.That(_stub.Count).IsEqualTo(1);
        await Assert.That(TokenEndpointClient.CreateDefaultHandler().AllowAutoRedirect).IsFalse();
    }

    [Test]
    public async Task Allowlisted_error_and_integer_error_codes_are_reported() {
        _stub.Respond = (_, _) => StubTokenEndpoint.Json(HttpStatusCode.BadRequest, "{\"error\":\"invalid_client\",\"error_codes\":[7000215],\"error_description\":\"AADSTS7000215: Invalid client secret\"}");
        var ex = await Assert.That(async () => await Client(Options()).RequestToken(default)).Throws<OAuthTokenException>();
        await Assert.That(ex!.Message).Contains("400");
        await Assert.That(ex.Message).Contains("invalid_client");
        await Assert.That(ex.Message).Contains("7000215");
        await Assert.That(ex.Message).Contains("idp.example.com");
        await Assert.That(ex.Message).DoesNotContain("AADSTS7000215: Invalid");
    }

    [Test]
    public async Task Error_echoing_the_secret_is_omitted() {
        _stub.Respond = (_, _) => StubTokenEndpoint.Json(HttpStatusCode.Unauthorized, "{\"error\":\"s3cr3t\",\"error_description\":\"bad s3cr3t\",\"error_uri\":\"https://x/s3cr3t\"}");
        var ex = await Assert.That(async () => await Client(Options()).RequestToken(default)).Throws<OAuthTokenException>();
        await Assert.That(ex!.Message).DoesNotContain("s3cr3t");
        await Assert.That(ex.Message).Contains("non-standard error (omitted)");
    }
}

[NotInParallel("global-logger")]
public class TokenEndpointClientLoggingTests {
    [Test]
    public async Task Provider_text_is_never_logged_above_debug_and_is_redacted_at_debug() {
        using var logs = new LogCapture();
        var assertionFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(assertionFile, "assertion-xyz");

        var stub = new StubTokenEndpoint {
            Respond = (_, _) => StubTokenEndpoint.Json(
                HttpStatusCode.BadRequest,
                "{\"error\":\"invalid_client\",\"error_description\":\"echo assertion-xyz\",\"error_uri\":\"https://x/assertion-xyz\"}"
            )
        };

        var options = new GrpcAuthOptions {
            Type = GrpcAuthType.OAuthClientCredentials, TokenEndpoint = "https://idp.example.com/t", ClientId = "c", ClientAssertionFile = assertionFile
        };

        var ex = await Assert.That(async () => await new TokenEndpointClient(options, stub, "sink").RequestToken(default)).Throws<OAuthTokenException>();

        await Assert.That(ex!.ToString()).DoesNotContain("assertion-xyz");
        await Assert.That(string.Join("\n", logs.TextAtOrAbove(Serilog.Events.LogEventLevel.Information))).DoesNotContain("assertion-xyz");
        await Assert.That(logs.AllText).DoesNotContain("assertion-xyz");
        await Assert.That(logs.AllText).Contains("echo ***");
        File.Delete(assertionFile);
    }
}
