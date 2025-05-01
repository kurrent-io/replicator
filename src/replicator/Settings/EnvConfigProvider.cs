using System.Collections;

namespace replicator.Settings; 

public class EnvConfigSource : IConfigurationSource {
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new EnvConfigProvider();
}

public class EnvConfigProvider : ConfigurationProvider {
    public override void Load() {
        var envVars = Environment.GetEnvironmentVariables();

        var vars = envVars.Cast<DictionaryEntry>()
            .Select(x => new EnvVar(x.Key.ToString()!, x.Value?.ToString()))
            .Where(x => x.Key.StartsWith("REPLICATOR_") && x.Value != null);

        Data = vars.ToDictionary(x => x.ConfigKey, x => x.Value, StringComparer.OrdinalIgnoreCase);
    }

    record EnvVar(string Key, string? Value) {
        public string ConfigKey {
            get {
                var newKey = Key.Replace("_", ":");
                Console.WriteLine($"{newKey} = {Value}");
                return newKey;
            }
        }
    }
}

public static class ConfigurationExtensions {
    public static IConfigurationBuilder AndEnvConfig(this IConfigurationBuilder builder)
        => builder.Add(new EnvConfigSource());
}