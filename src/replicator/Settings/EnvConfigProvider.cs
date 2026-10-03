using System.Collections;
using Kurrent.Replicator.Shared;

namespace replicator.Settings;

public class EnvConfigSource : IConfigurationSource {
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new EnvConfigProvider();
}

public class EnvConfigProvider : ConfigurationProvider {
    public override void Load() {
        var envVars = Environment.GetEnvironmentVariables();

        var vars = envVars.Cast<DictionaryEntry>()
            .Select(x => new EnvVar(x.Key.ToString()!, x.Value?.ToString()))
            .Where(x => x.Key.StartsWith("REPLICATOR_") && x.Value != null)
            .ToList();

        foreach (var v in vars) Console.WriteLine($"{v.ConfigKey} = {ConfigRedaction.Display(v.ConfigKey, v.Value)}");

        Data = vars.ToDictionary(x => x.ConfigKey, x => x.Value, StringComparer.OrdinalIgnoreCase);
    }

    record EnvVar(string Key, string? Value) {
        public string ConfigKey => Key.Replace("_", ":");
    }
}

public static class ConfigurationExtensions {
    public static IConfigurationBuilder AndEnvConfig(this IConfigurationBuilder builder)
        => builder.Add(new EnvConfigSource());
}
