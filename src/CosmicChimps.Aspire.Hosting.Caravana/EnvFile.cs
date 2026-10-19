namespace CosmicChimps.Aspire.Hosting.Caravana;

/// <summary>Reads the <c>.env</c> file Aspire writes next to the Compose file.</summary>
internal static class EnvFile
{
    /// <summary>The files to try, in order: Aspire's environment-specific file first.</summary>
    internal static readonly string[] CandidateNames = [".env.Production", ".env.production", ".env"];

    /// <summary><c>KEY=VALUE</c> lines; blank lines and <c>#</c> comments are skipped, matching quotes removed.</summary>
    internal static Dictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line["export ".Length..].TrimStart();

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1];
            values[key] = value;
        }
        return values;
    }

    /// <summary>The values of the first candidate file in <paramref name="directory"/> that holds any.</summary>
    internal static (string? Path, Dictionary<string, string> Values) LoadFirst(string directory)
    {
        foreach (var name in CandidateNames)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
                continue;
            var values = Parse(File.ReadAllText(path));
            if (values.Count > 0)
                return (path, values);
        }
        return (null, new Dictionary<string, string>(StringComparer.Ordinal));
    }
}
