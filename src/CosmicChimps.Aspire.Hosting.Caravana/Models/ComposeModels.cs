using System.Text.Json;
using System.Text.Json.Serialization;

namespace CosmicChimps.Aspire.Hosting.Caravana.Models;

// The request bodies of Caravana's API (api/openapi.yaml): ComposeRequest and ComposeOverlay.

/// <summary>The body of <c>POST /v1/apps/{id}/compose</c>.</summary>
internal sealed record ComposeRequest(
    [property: JsonPropertyName("compose")] string Compose,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("services")] IReadOnlyDictionary<string, ComposeOverlay> Services
);

/// <summary>What Compose has no words for, for one service.</summary>
internal sealed record ComposeOverlay
{
    [JsonPropertyName("domains")]
    public List<OverlayDomain>? Domains { get; init; }

    [JsonPropertyName("health")]
    public OverlayHealth? Health { get; init; }

    [JsonPropertyName("secrets")]
    public List<OverlaySecret>? Secrets { get; init; }

    [JsonPropertyName("backup")]
    public OverlayBackup? Backup { get; init; }

    [JsonPropertyName("identities")]
    public List<OverlayIdentity>? Identities { get; init; }

    internal bool IsEmpty =>
        Domains is null && Health is null && Secrets is null && Backup is null && Identities is null;
}

internal sealed record OverlayDomain(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int? Port
);

internal sealed record OverlayHealth(
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("port")] int? Port
);

/// <summary>Where a secret's value lives: the named secret provider (Bella Baxter) and the path of the secret in it.</summary>
internal sealed record SecretReference(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("path")] string Path
);

/// <summary>
/// One entry of an overlay's <c>secrets</c>, in exactly one of three shapes: a typed value
/// (<c>{name, value}</c>), a whole value read from a secret provider (<c>{name, from}</c>), or a template
/// that inserts provider values into other text (<c>{name, template, refs}</c>; <c>${ref}</c> inserts a
/// reference's value, <c>$$</c> is a literal dollar sign). <see cref="ToString"/> never prints a typed value;
/// a reference or a template isn't secret and may be shown.
/// </summary>
internal sealed record OverlaySecret
{
    /// <summary>A typed value.</summary>
    internal OverlaySecret(string name, string value)
    {
        Name = name;
        Value = value;
    }

    private OverlaySecret(string name) => Name = name;

    /// <summary>A whole value read from a secret provider.</summary>
    internal static OverlaySecret FromProvider(string name, SecretReference reference) =>
        new(name) { From = reference };

    /// <summary>A template that inserts the provider values named in <paramref name="refs"/>.</summary>
    internal static OverlaySecret FromTemplate(string name, string template, IReadOnlyDictionary<string, SecretReference> refs) =>
        new(name) { Template = template, Refs = refs };

    [JsonPropertyName("name")]
    public string Name { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("from")]
    public SecretReference? From { get; init; }

    [JsonPropertyName("template")]
    public string? Template { get; init; }

    [JsonPropertyName("refs")]
    public IReadOnlyDictionary<string, SecretReference>? Refs { get; init; }

    public override string ToString() =>
        (From, Template) switch
        {
            ({ } from, _) => $"OverlaySecret {{ Name = {Name}, From = {from.Provider}:{from.Path} }}",
            (_, { } template) =>
                $"OverlaySecret {{ Name = {Name}, Template = {template}, Refs = {string.Join(", ", Refs!.Select(r => $"{r.Key}={r.Value.Provider}:{r.Value.Path}"))} }}",
            _ => $"OverlaySecret {{ Name = {Name} }}",
        };
}

internal sealed record OverlayBackup(
    [property: JsonPropertyName("engine")] string Engine,
    [property: JsonPropertyName("schedule")] string? Schedule,
    [property: JsonPropertyName("keep")] int? Keep,
    [property: JsonPropertyName("restore")] OverlayRestore? Restore = null
);

/// <summary>A restore of the backup pinned under <see cref="From"/>, on <see cref="Schedule"/>.</summary>
internal sealed record OverlayRestore(
    [property: JsonPropertyName("schedule")] string Schedule,
    [property: JsonPropertyName("from")] string From
);

/// <summary>
/// A Nomad workload identity for each copy of the service: Nomad writes a signed token for
/// <see cref="Audience"/> to <c>/secrets/nomad_{name}.jwt</c> and renews it. <see cref="TtlSeconds"/> is
/// left out for Caravana's default.
/// </summary>
internal sealed record OverlayIdentity(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("audience")] IReadOnlyList<string> Audience,
    [property: JsonPropertyName("ttlSeconds")] int? TtlSeconds = null
);

internal static class CaravanaJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
