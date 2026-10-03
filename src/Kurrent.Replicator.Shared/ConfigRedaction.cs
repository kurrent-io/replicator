using System.Text.RegularExpressions;

namespace Kurrent.Replicator.Shared;

/// <summary>Decides what may be printed for a configuration value.</summary>
public static partial class ConfigRedaction {
    public static string? Display(string configKey, string? value) {
        var segments = configKey.Split(':');

        if (segments[^1].Equals("ConnectionString", StringComparison.OrdinalIgnoreCase)) return "***";

        if (value != null && ContainsCredentials(value)) return "***";

        var authIndex = Array.FindIndex(segments, s => s.Equals("Auth", StringComparison.OrdinalIgnoreCase));

        if (authIndex < 0) return value;

        var isAuthType = authIndex == segments.Length - 2 && segments[^1].Equals("Type", StringComparison.OrdinalIgnoreCase);

        return isAuthType ? value : "***";
    }

    // Any key: a URI with user info (scheme://user[:pass]@...), or a TCP connection string with DefaultUserCredentials.
    // A regex rather than Uri parsing, so custom and multi-host forms (mongodb+srv://, mongodb://u:p@h1,h2/db) match.
    static bool ContainsCredentials(string value)
        => UriWithUserInfo().IsMatch(value) || value.Contains("DefaultUserCredentials=", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.\-]*://[^/?#@\s]+@")]
    private static partial Regex UriWithUserInfo();
}
