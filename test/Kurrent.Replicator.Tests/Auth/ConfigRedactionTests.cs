using Kurrent.Replicator.Shared;

namespace Kurrent.Replicator.Tests.Auth;

public class ConfigRedactionTests {
    [Test]
    [Arguments("REPLICATOR:READER:CONNECTIONSTRING", "esdb://admin:changeit@host:2113")]
    [Arguments("REPLICATOR:READER:CONNECTIONSTRING", "GossipSeeds=a:2113; DefaultUserCredentials=admin:changeit;")]
    [Arguments("REPLICATOR:SINK:AUTH:CLIENTSECRET", "s3cr3t")]
    [Arguments("REPLICATOR:SINK:AUTH:ADDITIONALPARAMETERS:AUDIENCE", "kdb")]
    [Arguments("REPLICATOR:CHECKPOINT:PATH", "mongodb://u:p@mongo")]
    public async Task Sensitive_values_are_masked(string key, string value) {
        if (key.EndsWith("PATH")) {
            await Assert.That(ConfigRedaction.Display(key, value)).IsEqualTo(value); // not a connection string key: printed as before
            return;
        }

        await Assert.That(ConfigRedaction.Display(key, value)).IsEqualTo("***");
    }

    [Test]
    public async Task Auth_type_and_unrelated_keys_are_printed() {
        await Assert.That(ConfigRedaction.Display("REPLICATOR:SINK:AUTH:TYPE", "oauthTokenFile")).IsEqualTo("oauthTokenFile");
        await Assert.That(ConfigRedaction.Display("REPLICATOR:SINK:PARTITIONCOUNT", "4")).IsEqualTo("4");
        await Assert.That(ConfigRedaction.Display("REPLICATOR:SINK:AUTHORITY", "x")).IsEqualTo("x"); // segment match, not substring
    }
}
