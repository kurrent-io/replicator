namespace Kurrent.Replicator.Js;

public static class FunctionLoader {
    public static string? LoadFile(string? name, string description) {
        if (name == null) {
            return null;
        }

        if (!File.Exists(name)) {
            throw new ArgumentException($"{description} function file doesn't exist", nameof(name));
        }

        return File.ReadAllText(name);
    }
}
