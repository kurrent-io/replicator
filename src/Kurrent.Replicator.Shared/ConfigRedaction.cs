namespace Kurrent.Replicator.Shared;

/// <summary>Decides what may be printed for a configuration value.</summary>
public static class ConfigRedaction {
    public static string? Display(string configKey, string? value) {
        var segments = configKey.Split(':');

        if (segments[^1].Equals("ConnectionString", StringComparison.OrdinalIgnoreCase)) return "***";

        var authIndex = Array.FindIndex(segments, s => s.Equals("Auth", StringComparison.OrdinalIgnoreCase));

        if (authIndex < 0) return value;

        var isAuthType = authIndex == segments.Length - 2 && segments[^1].Equals("Type", StringComparison.OrdinalIgnoreCase);

        return isAuthType ? value : "***";
    }
}
