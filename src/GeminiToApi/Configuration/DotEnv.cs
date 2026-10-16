namespace GeminiToApi.Configuration;

/// <summary>Minimal <c>.env</c> reader/writer (KEY=VALUE lines, # comments, optional quotes).</summary>
public static class DotEnv
{
    public static Dictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!File.Exists(path)) return values;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();

            var eq = line.IndexOf('=');

            if (eq <= 0) continue;

            var key   = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
            {
                var quote = value[0];
                value = value[1..^1];
                if (quote == '"') value = value.Replace("\\\"", "\"").Replace("\\n", "\n");
            }
            else
            {
                var comment = value.IndexOf(" #", StringComparison.Ordinal);
                if (comment >= 0) value = value[..comment].TrimEnd();
            }

            values[key] = value;
        }

        return values;
    }

    /// <summary>Writes (or updates) the given keys, keeping any other line of an existing file.</summary>
    public static void Write(string path, IReadOnlyDictionary<string, string> values)
    {
        var lines   = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        var pending = new Dictionary<string, string>(values, StringComparer.Ordinal);

        for (var i = 0; i < lines.Count; i++)
        {
            var eq = lines[i].IndexOf('=');

            if (eq <= 0 || lines[i].TrimStart().StartsWith('#')) continue;

            var key = lines[i][..eq].Trim();

            if (pending.Remove(key, out var value)) lines[i] = key + "=" + Quote(value);
        }

        foreach (var (key, value) in pending) lines.Add(key + "=" + Quote(value));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllLines(path, lines);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
