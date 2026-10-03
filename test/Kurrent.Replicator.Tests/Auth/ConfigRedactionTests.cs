using Kurrent.Replicator.Shared;

namespace Kurrent.Replicator.Tests.Auth;

public class ConfigRedactionTests {
    [Test]
    [Arguments("REPLICATOR:READER:CONNECTIONSTRING", "esdb://admin:changeit@host:2113")]
    [Arguments("REPLICATOR:READER:CONNECTIONSTRING", "GossipSeeds=a:2113; DefaultUserCredentials=admin:changeit;")]
    [Arguments("REPLICATOR:SINK:AUTH:CLIENTSECRET", "s3cr3t")]
    [Arguments("REPLICATOR:SINK:AUTH:ADDITIONALPARAMETERS:AUDIENCE", "kdb")]
    [Arguments("REPLICATOR:CHECKPOINT:PATH", "mongodb://u:p@mongo")]
    [Arguments("REPLICATOR:CHECKPOINT:PATH", "mongodb+srv://u:p@cluster0.example.net/replicator?retryWrites=true")]
    [Arguments("REPLICATOR:CHECKPOINT:PATH", "mongodb://u:p@mongo1:27017,mongo2:27017/db?replicaSet=rs0")]
    [Arguments("REPLICATOR:CHECKPOINT:PATH", "MONGODB://user@mongo")]
    [Arguments("REPLICATOR:SOMETHING:ELSE", "esdb+discover://admin:changeit@cluster:2113")]
    [Arguments("REPLICATOR:SOMETHING:ELSE", "ConnectTo=tcp://host:1113; defaultusercredentials=admin:changeit")]
    public async Task Sensitive_values_are_masked(string key, string value) {
        await Assert.That(ConfigRedaction.Display(key, value)).IsEqualTo("***");
    }

    [Test]
    [Arguments("REPLICATOR:CHECKPOINT:PATH", "mongodb://mongo")]
    [Arguments("REPLICATOR:CHECKPOINT:PATH", "mongodb://mongo:27017/replicator?authSource=admin")]
    [Arguments("REPLICATOR:CHECKPOINT:PATH", "./checkpoint")]
    [Arguments("REPLICATOR:CHECKPOINT:PATH", "/data/checkpoint/position.txt")]
    [Arguments("REPLICATOR:SINK:AUTH:TYPE", "oauthTokenFile")]
    [Arguments("REPLICATOR:SINK:PARTITIONCOUNT", "4")]
    [Arguments("REPLICATOR:SINK:AUTHORITY", "x")] // segment match, not substring
    [Arguments("REPLICATOR:TRANSFORM:CONFIG", "https://transform.example.com/run")]
    public async Task Values_without_credentials_are_printed(string key, string value) {
        await Assert.That(ConfigRedaction.Display(key, value)).IsEqualTo(value);
    }
}
