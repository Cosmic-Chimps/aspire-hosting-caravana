using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;

namespace CosmicChimps.Aspire.Hosting.Caravana;

/// <summary>The Caravana deployment target of an Aspire application.</summary>
/// <remarks>
/// The string properties are the <b>resolved</b> configuration, filled by
/// <see cref="ResolveConfigurationAsync"/> at the start of the deploy step. Their deferred sources
/// (literals or Aspire parameters) are the internal <c>*Source</c> fields, so nothing reads a
/// parameter while the application model is being built.
/// </remarks>
public sealed class CaravanaResource : Resource
{
    public CaravanaResource(string name)
        : base(name) { }

    /// <summary>Caravana's address (resolved).</summary>
    public string Url { get; private set; } = string.Empty;

    /// <summary>The API token (resolved). Never logged.</summary>
    public string Token { get; private set; } = string.Empty;

    /// <summary>The project (resolved). Default <c>default</c>.</summary>
    public string Project { get; private set; } = CaravanaPlace.DefaultProject;

    /// <summary>The environment (resolved). Default <c>production</c>.</summary>
    public string Environment { get; private set; } = CaravanaPlace.DefaultEnvironment;

    /// <summary>The app's name in the environment (resolved).</summary>
    public string AppName { get; private set; } = string.Empty;

    /// <summary>The console's display name (resolved). Empty means the app ID.</summary>
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>The Docker Compose environment that generates the Compose file and the .env file.</summary>
    public DockerComposeEnvironmentResource ComposeEnvironment { get; internal set; } = null!;

    internal IResourceBuilder<DockerComposeEnvironmentResource> ComposeEnvironmentBuilder { get; set; } = null!;

    /// <summary>Deploy the Aspire dashboard too. See <see cref="CaravanaSettings.DeployDashboard"/>.</summary>
    public bool DeployDashboard { get; set; }

    /// <summary>Wait for the deployment. See <see cref="CaravanaSettings.WaitForDeployment"/>.</summary>
    public bool WaitForDeployment { get; set; } = true;

    /// <summary>Fail when a healthy newer deployment superseded this one. See <see cref="CaravanaSettings.FailOnSuperseded"/>.</summary>
    public bool FailOnSuperseded { get; set; }

    /// <summary>The wait limit. See <see cref="CaravanaSettings.DeploymentTimeout"/>.</summary>
    public TimeSpan DeploymentTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Delay between deployment polls. Internal so tests can shorten it.</summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    internal CaravanaValue? UrlSource { get; set; }
    internal CaravanaValue? TokenSource { get; set; }
    internal CaravanaValue? ProjectSource { get; set; }
    internal CaravanaValue? EnvironmentSource { get; set; }
    internal CaravanaValue? AppNameSource { get; set; }
    internal CaravanaValue? DisplayNameSource { get; set; }

    /// <summary>The fallback app name: the name given to <c>PublishToCaravana</c>.</summary>
    internal string DefaultAppName { get; set; } = string.Empty;

    /// <summary>Resolves every deferred setting. Runs first in the deploy step, before validation.</summary>
    internal async Task ResolveConfigurationAsync(CancellationToken ct)
    {
        Url = await UrlSource.ResolveRequiredAsync("Url", ct);
        Token = await TokenSource.ResolveRequiredAsync("Token", ct);
        Project = await ProjectSource.ResolveOrDefaultAsync(CaravanaPlace.DefaultProject, ct);
        Environment = await EnvironmentSource.ResolveOrDefaultAsync(CaravanaPlace.DefaultEnvironment, ct);
        AppName = await AppNameSource.ResolveOrDefaultAsync(DefaultAppName, ct);
        DisplayName = (await DisplayNameSource.ResolveAsync(ct))?.Trim() ?? string.Empty;
    }
}
