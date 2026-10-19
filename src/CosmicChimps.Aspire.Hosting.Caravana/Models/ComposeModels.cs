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

    internal bool IsEmpty => Domains is null && Health is null && Secrets is null && Backup is null;
}

internal sealed record OverlayDomain(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int? Port
);

internal sealed record OverlayHealth(
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("port")] int? Port
);

/// <summary>A secret with its value. <see cref="ToString"/> never prints the value.</summary>
internal sealed record OverlaySecret(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value
)
{
    public override string ToString() => $"OverlaySecret {{ Name = {Name} }}";
}

internal sealed record OverlayBackup(
    [property: JsonPropertyName("engine")] string Engine,
    [property: JsonPropertyName("schedule")] string? Schedule,
    [property: JsonPropertyName("keep")] int? Keep
);

internal static class CaravanaJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
