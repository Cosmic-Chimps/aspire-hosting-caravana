using System.Text;
using System.Text.RegularExpressions;
using CosmicChimps.Aspire.Hosting.Caravana.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace CosmicChimps.Aspire.Hosting.Caravana;

/// <summary>What the owner said about one service (resolved: no parameters left).</summary>
internal sealed record CaravanaServiceSettings
{
    internal List<(string Host, int? Port)> Domains { get; } = [];

    internal bool HasHealth { get; set; }
    internal string? HealthPath { get; set; }
    internal int? HealthPort { get; set; }

    internal CaravanaBackupEngine? BackupEngine { get; set; }
    internal string? BackupSchedule { get; set; }
    internal int? BackupKeep { get; set; }

    internal int? ReservedMB { get; set; }
    internal int? MaxMB { get; set; }

    internal bool Exclude { get; set; }
}

internal sealed record TransformInput(
    string ComposeYaml,
    IReadOnlyDictionary<string, string> EnvValues,
    IReadOnlyDictionary<string, string> SecretValues,
    bool DeployDashboard,
    IReadOnlyDictionary<string, CaravanaServiceSettings> Services
);

internal sealed record TransformResult(
    string? Compose,
    IReadOnlyDictionary<string, ComposeOverlay> Services,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings
)
{
    internal bool Success => Errors.Count == 0;
}

/// <summary>
/// Turns Aspire's generated Compose file into what Caravana's <c>deployCompose</c> takes: the file
/// with every variable resolved, and the overlay. Pure: no I/O, no environment, no logging.
/// </summary>
/// <remarks>
/// Steps: (1) substitute every <c>${VAR}</c> (a variable without a value is an error naming it);
/// (2) drop the dashboard unless asked for and every excluded resource, with their
/// <c>depends_on</c> entries; (3) rename volumes to stable, valid names; (4) move every
/// environment entry that holds a secret's value to the overlay's secrets; (5) memory goes into the
/// file; domains, health and backup into the overlay.
/// </remarks>
internal static partial class CaravanaComposeTransformer
{
    // ${VAR}, ${VAR:-default}, ${VAR-default}; "$$" is Compose's escape for a literal "$".
    [GeneratedRegex(@"\$\$|\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:(?<op>:?-)(?<default>[^}]*))?\}")]
    private static partial Regex VariablePattern();

    // The AppHost prefix Aspire puts before a volume's own name: "<apphost>-<10 hex>-<name>".
    [GeneratedRegex(@"^.*?-[0-9a-f]{10}-(?<name>.+)$")]
    private static partial Regex VolumePrefixPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SecretNamePattern();

    [GeneratedRegex(@"[^a-z0-9_-]")]
    private static partial Regex InvalidVolumeChars();

    // Secrets shorter than this only match an environment value that is exactly the secret.
    private const int MinSubstringSecretLength = 4;

    internal static TransformResult Transform(TransformInput input)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        YamlStream stream;
        try
        {
            stream = new YamlStream();
            stream.Load(new StringReader(input.ComposeYaml));
        }
        catch (YamlException ex)
        {
            return Failed($"The Compose file is not valid YAML: {ex.Message}");
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            return Failed("The Compose file must be one YAML document that is a mapping.");
        if (Get(root, "services") is not YamlMappingNode services)
            return Failed("The Compose file has no 'services'.");

        // 1. Variables.
        var variables = new Dictionary<string, string>(input.EnvValues, StringComparer.Ordinal);
        foreach (var (key, value) in input.SecretValues)
        {
            if (!string.IsNullOrEmpty(value))
                variables[key] = value;
        }
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        Substitute(root, variables, missing);
        foreach (var name in missing)
            errors.Add(
                $"The Compose file refers to ${{{name}}}, which has no value: it is not in the .env file "
                    + "Aspire wrote (is the parameter set?)."
            );

        // 2. Drop the dashboard and the excluded resources.
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var excludedByOwner = new List<string>();
        string? dashboardName = null;
        foreach (var (key, node) in services.Children.Select(kv => (Name: Name(kv.Key), kv.Value)))
        {
            if (IsDashboard(node))
            {
                dashboardName = key;
                if (!input.DeployDashboard)
                    dropped.Add(key);
            }
            if (input.Services.TryGetValue(key, out var s) && s.Exclude)
            {
                dropped.Add(key);
                excludedByOwner.Add(key);
            }
        }

        foreach (var (name, settings) in input.Services)
        {
            if (Get(services, name) is null)
            {
                if (settings.Exclude)
                    warnings.Add($"WithCaravanaExclude on '{name}': the Compose file has no such service.");
                else if (!dropped.Contains(name))
                    errors.Add(
                        $"A Caravana setting names resource '{name}', but the Compose file has no service of that name."
                    );
            }
        }

        foreach (var name in dropped)
            services.Children.Remove(new YamlScalarNode(name));

        foreach (var node in services.Children.Values.OfType<YamlMappingNode>())
        {
            DropDependsOn(node, dropped);
            if (dashboardName is not null && dropped.Contains(dashboardName))
                DropEnvironmentReferences(node, dashboardName);
        }
        foreach (var excluded in excludedByOwner)
        {
            foreach (var (svcName, node) in services.Children.Select(kv => (Name(kv.Key), kv.Value)))
            {
                if (node is YamlMappingNode svc && EnvironmentReferences(svc, excluded) is { } variable)
                    warnings.Add(
                        $"Service '{svcName}' still refers to the excluded service '{excluded}' in {variable}."
                    );
            }
        }

        DropUnusedVolumes(root, services);

        // 3. Volumes: stable, valid names.
        RenameVolumes(root, services);

        // 4. Secrets: move every environment entry that holds a secret's value to the overlay.
        var secretValues = input
            .SecretValues.Where(kv => !string.IsNullOrEmpty(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var overlay = new Dictionary<string, ComposeOverlay>(StringComparer.Ordinal);
        var movedSecrets = new Dictionary<string, List<OverlaySecret>>(StringComparer.Ordinal);

        foreach (var (svcName, node) in services.Children.Select(kv => (Name(kv.Key), kv.Value)))
        {
            if (node is not YamlMappingNode svc)
                continue;
            var moved = MoveSecrets(svcName, svc, secretValues, errors);
            if (moved.Count > 0)
                movedSecrets[svcName] = moved;
        }

        // A secret in a command, a label or any other field would be sent in the clear.
        foreach (var (secretKey, secretValue) in secretValues)
        {
            if (secretValue.Length < MinSubstringSecretLength)
                continue;
            var where = FindScalar(root, "", secretValue);
            if (where is not null)
                errors.Add(
                    $"The value of secret parameter '{secretKey}' appears in {where}. Caravana keeps secrets as "
                        + "environment variables, so a secret can't be part of a command, label or other field."
                );
        }

        // 5. Memory into the file; domains, health and backup into the overlay.
        foreach (var (svcName, node) in services.Children.Select(kv => (Name(kv.Key), kv.Value)))
        {
            if (node is not YamlMappingNode svc)
                continue;
            input.Services.TryGetValue(svcName, out var settings);

            if (settings?.ReservedMB is { } reserved)
            {
                Set(svc, "mem_reservation", Quoted($"{reserved}m"));
                if (settings.MaxMB is { } max)
                    Set(svc, "mem_limit", Quoted($"{max}m"));
            }

            var ports = ContainerPorts(svc);
            var entry = BuildOverlay(
                settings,
                ports,
                movedSecrets.GetValueOrDefault(svcName)
            );
            if (!entry.IsEmpty)
                overlay[svcName] = entry;
        }

        if (errors.Count > 0)
            return new TransformResult(null, overlay, errors, warnings);

        return new TransformResult(Serialize(stream), overlay, errors, warnings);

        TransformResult Failed(string error) =>
            new(null, new Dictionary<string, ComposeOverlay>(), [error], warnings);
    }

    // ── 1. Variables ─────────────────────────────────────────────────────────

    private static void Substitute(YamlNode node, Dictionary<string, string> variables, SortedSet<string> missing)
    {
        switch (node)
        {
            case YamlScalarNode scalar when scalar.Value is { } text && text.Contains('$'):
                scalar.Value = VariablePattern()
                    .Replace(
                        text,
                        m =>
                        {
                            if (!m.Groups["name"].Success)
                                return m.Value; // "$$" stays: Caravana reads it as a literal "$".
                            var name = m.Groups["name"].Value;
                            var op = m.Groups["op"].Value;
                            var hasValue = variables.TryGetValue(name, out var value);
                            if (op.Length == 0)
                            {
                                // ${VAR}: a variable with no value, or an empty one, is an error.
                                if (hasValue && value!.Length > 0)
                                    return Escape(value);
                                missing.Add(name);
                                return m.Value;
                            }

                            // ${VAR:-default} uses the default when unset or empty; ${VAR-default} when unset.
                            var useValue = op == ":-" ? !string.IsNullOrEmpty(value) : hasValue;
                            return useValue ? Escape(value!) : m.Groups["default"].Value;
                        }
                    );
                break;
            case YamlMappingNode mapping:
                // Only values: changing a key in place would corrupt the mapping, and Aspire writes
                // no variables in keys.
                foreach (var value in mapping.Children.Values)
                    Substitute(value, variables, missing);
                break;
            case YamlSequenceNode sequence:
                foreach (var child in sequence.Children)
                    Substitute(child, variables, missing);
                break;
        }
    }

    // Caravana reads the file as Compose does, so a dollar sign that came from a value (not from the
    // file) must be written doubled or it would be read as the start of a variable.
    private static string Escape(string value) => value.Replace("$", "$$", StringComparison.Ordinal);

    private static string Unescape(string text) => text.Replace("$$", "$", StringComparison.Ordinal);

    // ── 2. Dropping services ─────────────────────────────────────────────────

    private static bool IsDashboard(YamlNode service) =>
        service is YamlMappingNode m
        && Get(m, "image") is YamlScalarNode { Value: { } image }
        && image.Contains("aspire-dashboard", StringComparison.OrdinalIgnoreCase);

    private static void DropDependsOn(YamlMappingNode service, HashSet<string> dropped)
    {
        switch (Get(service, "depends_on"))
        {
            case YamlMappingNode map:
                foreach (var key in map.Children.Keys.Where(k => dropped.Contains(Name(k))).ToList())
                    map.Children.Remove(key);
                if (map.Children.Count == 0)
                    service.Children.Remove(new YamlScalarNode("depends_on"));
                break;
            case YamlSequenceNode seq:
                foreach (var child in seq.Children.Where(c => dropped.Contains(Name(c))).ToList())
                    seq.Children.Remove(child);
                if (seq.Children.Count == 0)
                    service.Children.Remove(new YamlScalarNode("depends_on"));
                break;
        }
    }

    // The dashboard is a telemetry sink; without it, the OTEL endpoint that points at it has nowhere to go.
    private static void DropEnvironmentReferences(YamlMappingNode service, string dashboardName)
    {
        if (Get(service, "environment") is YamlMappingNode env)
        {
            foreach (var (key, value) in env.Children.ToList())
            {
                if (value is YamlScalarNode { Value: { } text } && RefersTo(text, dashboardName))
                    env.Children.Remove(key);
            }
            if (env.Children.Count == 0)
                service.Children.Remove(new YamlScalarNode("environment"));
        }
    }

    private static string? EnvironmentReferences(YamlMappingNode service, string serviceName)
    {
        if (Get(service, "environment") is not YamlMappingNode env)
            return null;
        foreach (var (key, value) in env.Children)
        {
            if (value is YamlScalarNode { Value: { } text } && RefersTo(text, serviceName))
                return $"environment variable '{Name(key)}'";
        }
        return null;
    }

    private static bool RefersTo(string text, string serviceName) =>
        Regex.IsMatch(text, $"(?<![A-Za-z0-9_-]){Regex.Escape(serviceName)}(?![A-Za-z0-9_-])");

    private static void DropUnusedVolumes(YamlMappingNode root, YamlMappingNode services)
    {
        if (Get(root, "volumes") is not YamlMappingNode volumes)
            return;

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var svc in services.Children.Values.OfType<YamlMappingNode>())
        {
            foreach (var reference in VolumeReferences(svc))
                used.Add(reference.Source);
        }

        foreach (var key in volumes.Children.Keys.Where(k => !used.Contains(Name(k))).ToList())
            volumes.Children.Remove(key);
        if (volumes.Children.Count == 0)
            root.Children.Remove(new YamlScalarNode("volumes"));
    }

    // ── 3. Volumes ───────────────────────────────────────────────────────────

    private static void RenameVolumes(YamlMappingNode root, YamlMappingNode services)
    {
        if (Get(root, "volumes") is not YamlMappingNode volumes)
            return;

        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var original in volumes.Children.Keys.Select(Name))
        {
            var stable = StableVolumeName(original);
            var name = stable;
            for (var n = 2; !taken.Add(name); n++)
            {
                var suffix = $"-{n}";
                name = stable[..Math.Min(stable.Length, 63 - suffix.Length)] + suffix;
            }
            renames[original] = name;
        }

        // Rebuild the top-level mapping in order, with the new keys.
        // The new definitions are empty: Aspire writes only `driver: local`, which is Caravana's
        // default, and a carried-over `name:` would defeat the rename.
        foreach (var key in volumes.Children.Keys.ToList())
            volumes.Children.Remove(key);
        foreach (var (_, renamed) in renames)
            volumes.Children.Add(new YamlScalarNode(renamed), new YamlMappingNode());

        foreach (var svc in services.Children.Values.OfType<YamlMappingNode>())
        {
            foreach (var reference in VolumeReferences(svc))
            {
                if (renames.TryGetValue(reference.Source, out var renamed))
                    reference.Rename(renamed);
            }
        }
    }

    /// <summary>The stable, valid name for an Aspire volume: <c>apphost-hash-seq-data</c> becomes <c>seq-data</c>.</summary>
    internal static string StableVolumeName(string original)
    {
        var name = original;
        var m = VolumePrefixPattern().Match(name);
        if (m.Success)
            name = m.Groups["name"].Value;

        name = InvalidVolumeChars().Replace(name.ToLowerInvariant(), "-").TrimStart('-', '_');
        if (name.Length > 63)
            name = name[..63];
        return name.Length == 0 ? "volume" : name;
    }

    private sealed record VolumeReference(string Source, Action<string> Rename);

    private static IEnumerable<VolumeReference> VolumeReferences(YamlMappingNode service)
    {
        if (Get(service, "volumes") is not YamlSequenceNode list)
            yield break;

        for (var i = 0; i < list.Children.Count; i++)
        {
            switch (list.Children[i])
            {
                case YamlMappingNode map
                    when (Get(map, "type") as YamlScalarNode)?.Value is null or "volume"
                        && Get(map, "source") is YamlScalarNode { Value: { } source } sourceNode:
                    yield return new VolumeReference(source, newName => sourceNode.Value = newName);
                    break;
                case YamlScalarNode { Value: { } text } scalar:
                    var colon = text.IndexOf(':');
                    if (colon <= 0)
                        break; // an anonymous volume
                    var src = text[..colon];
                    if (src.StartsWith('/') || src.StartsWith('.') || src.StartsWith('~'))
                        break; // a bind mount
                    var rest = text[colon..];
                    yield return new VolumeReference(src, newName => scalar.Value = newName + rest);
                    break;
            }
        }
    }

    // ── 4. Secrets ───────────────────────────────────────────────────────────

    private static bool HoldsSecret(string escaped, IReadOnlyDictionary<string, string> secrets)
    {
        var value = Unescape(escaped);
        return secrets.Values.Any(s => value == s || (s.Length >= MinSubstringSecretLength && value.Contains(s, StringComparison.Ordinal)));
    }

    private static List<OverlaySecret> MoveSecrets(
        string serviceName,
        YamlMappingNode service,
        IReadOnlyDictionary<string, string> secrets,
        List<string> errors
    )
    {
        var moved = new List<OverlaySecret>();
        if (secrets.Count == 0)
            return moved;

        void Move(string key, string value)
        {
            if (!SecretNamePattern().IsMatch(key))
            {
                errors.Add(
                    $"Environment variable '{key}' of service '{serviceName}' holds a secret, but its name is not a "
                        + "valid Caravana secret name (letters, digits and '_', not starting with a digit)."
                );
                return;
            }
            moved.Add(new OverlaySecret(key, Unescape(value)));
        }

        switch (Get(service, "environment"))
        {
            case YamlMappingNode env:
                foreach (var (key, value) in env.Children.ToList())
                {
                    if (value is YamlScalarNode { Value: { } text } && HoldsSecret(text, secrets))
                    {
                        Move(Name(key), text);
                        env.Children.Remove(key);
                    }
                }
                if (env.Children.Count == 0)
                    service.Children.Remove(new YamlScalarNode("environment"));
                break;

            case YamlSequenceNode list:
                foreach (var item in list.Children.ToList())
                {
                    if (item is not YamlScalarNode { Value: { } line })
                        continue;
                    var eq = line.IndexOf('=');
                    if (eq > 0 && HoldsSecret(line[(eq + 1)..], secrets))
                    {
                        Move(line[..eq], line[(eq + 1)..]);
                        list.Children.Remove(item);
                    }
                }
                if (list.Children.Count == 0)
                    service.Children.Remove(new YamlScalarNode("environment"));
                break;
        }

        return moved;
    }

    /// <summary>The path of the first scalar that contains <paramref name="needle"/>, or null.</summary>
    private static string? FindScalar(YamlNode node, string path, string needle)
    {
        switch (node)
        {
            case YamlScalarNode { Value: { } text } when Unescape(text).Contains(needle, StringComparison.Ordinal):
                return path.Length == 0 ? "the Compose file" : path;
            case YamlMappingNode mapping:
                foreach (var (key, value) in mapping.Children)
                {
                    var childPath = path.Length == 0 ? Name(key) : $"{path}.{Name(key)}";
                    if (FindScalar(key, childPath, needle) is not null)
                        return childPath + " (a key)";
                    if (FindScalar(value, childPath, needle) is { } found)
                        return found;
                }
                break;
            case YamlSequenceNode sequence:
                for (var i = 0; i < sequence.Children.Count; i++)
                {
                    if (FindScalar(sequence.Children[i], $"{path}[{i}]", needle) is { } found)
                        return found;
                }
                break;
        }
        return null;
    }

    // ── 5. Overlay ───────────────────────────────────────────────────────────

    private static ComposeOverlay BuildOverlay(
        CaravanaServiceSettings? settings,
        IReadOnlyList<int> ports,
        List<OverlaySecret>? secrets
    )
    {
        // With more than one port, Caravana can't guess which one the domain and the health check
        // mean. The first published port (else the first exposed one) is the service's front.
        int? front = ports.Count > 1 ? ports[0] : null;

        List<OverlayDomain>? domains = null;
        if (settings is { Domains.Count: > 0 })
            domains = settings.Domains.Select(d => new OverlayDomain(d.Host, d.Port ?? front)).ToList();

        OverlayHealth? health = null;
        if (settings is { HasHealth: true })
            health = new OverlayHealth(settings.HealthPath, settings.HealthPort ?? front);
        else if (front is not null)
            health = new OverlayHealth(null, front);

        OverlayBackup? backup = null;
        if (settings?.BackupEngine is { } engine)
            backup = new OverlayBackup(
                engine == CaravanaBackupEngine.Postgres ? "postgres" : "volume",
                settings.BackupSchedule,
                settings.BackupKeep
            );

        return new ComposeOverlay
        {
            Domains = domains,
            Health = health,
            Secrets = secrets is { Count: > 0 } ? secrets : null,
            Backup = backup,
        };
    }

    /// <summary>The container ports a service lists: published ones first, then exposed ones; distinct.</summary>
    private static List<int> ContainerPorts(YamlMappingNode service)
    {
        var ports = new List<int>();
        foreach (var key in new[] { "ports", "expose" })
        {
            if (Get(service, key) is not YamlSequenceNode list)
                continue;
            foreach (var item in list.Children)
            {
                string? text = item switch
                {
                    YamlScalarNode s => s.Value,
                    YamlMappingNode m => (Get(m, "target") as YamlScalarNode)?.Value,
                    _ => null,
                };
                if (text is null)
                    continue;
                var slash = text.IndexOf('/');
                if (slash >= 0)
                    text = text[..slash];
                var colon = text.LastIndexOf(':');
                if (colon >= 0)
                    text = text[(colon + 1)..];
                if (int.TryParse(text, out var port) && !ports.Contains(port))
                    ports.Add(port);
            }
        }
        return ports;
    }

    // ── YAML helpers ─────────────────────────────────────────────────────────

    private static string Name(YamlNode node) => (node as YamlScalarNode)?.Value ?? string.Empty;

    private static YamlNode? Get(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static void Set(YamlMappingNode mapping, string key, YamlNode value) =>
        mapping.Children[new YamlScalarNode(key)] = value;

    private static YamlScalarNode Quoted(string value) =>
        new(value) { Style = ScalarStyle.DoubleQuoted };

    private static string Serialize(YamlStream stream)
    {
        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb))
            stream.Save(writer, assignAnchors: false);

        // Save() writes the document markers; the file is a single document without them.
        var text = sb.ToString().Replace("\r\n", "\n");
        var lines = text.Split('\n').ToList();
        if (lines.Count > 0 && lines[0] == "---")
            lines.RemoveAt(0);
        var end = lines.FindLastIndex(l => l == "...");
        if (end >= 0)
            lines.RemoveAt(end);
        return string.Join('\n', lines).TrimEnd() + "\n";
    }
}
