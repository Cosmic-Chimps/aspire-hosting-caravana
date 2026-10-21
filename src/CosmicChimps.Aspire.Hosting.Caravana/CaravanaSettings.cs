namespace CosmicChimps.Aspire.Hosting.Caravana;

/// <summary>
/// What <c>PublishToCaravana</c> needs to know. Every text setting takes a literal or an Aspire
/// parameter (see <see cref="CaravanaValue"/>).
/// </summary>
public sealed class CaravanaSettings
{
    /// <summary>Caravana's address, e.g. <c>https://caravana.example.com</c>. Required.</summary>
    public CaravanaValue? Url { get; set; }

    /// <summary>
    /// An API token (<c>cvat_...</c>). Required. A full token can create the project and
    /// environment; a deploy token (made with a scope) can only deploy an app that already has them.
    /// </summary>
    public CaravanaValue? Token { get; set; }

    /// <summary>The Caravana project. Defaults to <c>default</c>. Created if missing (full token).</summary>
    public CaravanaValue? Project { get; set; }

    /// <summary>The environment of the project. Defaults to <c>production</c>. Created if missing (full token).</summary>
    public CaravanaValue? Environment { get; set; }

    /// <summary>The app's name in its environment. Defaults to the name given to <c>PublishToCaravana</c>.</summary>
    public CaravanaValue? AppName { get; set; }

    /// <summary>The name the console shows. Empty means the app's ID.</summary>
    public CaravanaValue? DisplayName { get; set; }

    /// <summary>
    /// Deploy the Aspire dashboard as a service too. Off by default: it shows every service's
    /// telemetry and has no sign-in unless you configure one.
    /// </summary>
    public bool DeployDashboard { get; set; }

    /// <summary>Wait until the deployment is healthy (and fail the step if it is not). Default true.</summary>
    public bool WaitForDeployment { get; set; } = true;

    /// <summary>
    /// Fail the step when a newer deployment replaced this one, even if that one is healthy. Default
    /// false: Caravana's newest deployment wins, so the step passes with a warning when the newer one
    /// is healthy, and fails when it is not (or can't be read).
    /// </summary>
    public bool FailOnSuperseded { get; set; }

    /// <summary>How long to wait for the deployment. Default 10 minutes.</summary>
    public TimeSpan DeploymentTimeout { get; set; } = TimeSpan.FromMinutes(10);
}
