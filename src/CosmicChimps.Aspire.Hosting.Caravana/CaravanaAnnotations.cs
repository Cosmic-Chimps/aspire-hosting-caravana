using Aspire.Hosting.ApplicationModel;

namespace CosmicChimps.Aspire.Hosting.Caravana;

/// <summary>A database or volume backup policy, as Caravana's <c>BackupPolicy</c>.</summary>
public enum CaravanaBackupEngine
{
    /// <summary>A <c>pg_dump</c> of a Postgres service (needs <c>POSTGRES_PASSWORD</c>).</summary>
    Postgres,

    /// <summary>The service's volumes archived while it is stopped for a few seconds.</summary>
    Volume,
}

// The per-resource settings live on the CaravanaResource, keyed by the resource name (which is the
// service name in the generated Compose file), because the Caravana target is what reads them.

internal abstract class CaravanaServiceAnnotation : IResourceAnnotation
{
    /// <summary>The Aspire resource name, which is the Compose service name (lower case).</summary>
    public required string ServiceName { get; init; }
}

internal sealed class CaravanaDomainAnnotation : CaravanaServiceAnnotation
{
    /// <summary>The host: a literal, or a parameter resolved when the deploy runs.</summary>
    public required CaravanaValue Host { get; init; }

    /// <summary>The container port the host routes to; null when the service has one port.</summary>
    public int? Port { get; init; }
}

internal sealed class CaravanaHealthAnnotation : CaravanaServiceAnnotation
{
    public string? Path { get; init; }
    public int? Port { get; init; }
}

internal sealed class CaravanaBackupAnnotation : CaravanaServiceAnnotation
{
    public required CaravanaBackupEngine Engine { get; init; }
    public string? Schedule { get; init; }
    public int? Keep { get; init; }
}

internal sealed class CaravanaMemoryAnnotation : CaravanaServiceAnnotation
{
    public required int ReservedMB { get; init; }
    public int? MaxMB { get; init; }
}

internal sealed class CaravanaExcludeAnnotation : CaravanaServiceAnnotation;
