using System.Diagnostics;
using CosmicChimps.Aspire.Hosting.Caravana.Models;
using Microsoft.Extensions.Logging;

namespace CosmicChimps.Aspire.Hosting.Caravana;

/// <summary>A deploy the publisher gave up on, with the reason to show. Never holds a secret.</summary>
internal sealed class CaravanaDeployException(string message) : Exception(message);

internal sealed record DeployPlan(
    string Project,
    string Environment,
    string AppName,
    ComposeRequest Body,
    bool Wait,
    TimeSpan Timeout,
    TimeSpan PollInterval,
    IReadOnlyDictionary<string, string> OriginHeaders,
    bool FailOnSuperseded = false
);

/// <summary>
/// The deploy: make sure the project and environment exist, create or update the app from the
/// Compose request, and wait for the deployment.
/// </summary>
internal sealed class CaravanaDeployFlow(CaravanaApiClient api, SecretRedactor redactor, ILogger logger)
{
    /// <summary>How many times a superseded deployment's successor is itself superseded before the step stops following.</summary>
    private const int MaxSupersedeHops = 5;

    internal async Task<DeploymentInfo> RunAsync(DeployPlan plan, CancellationToken ct)
    {
        var appId = CaravanaPlace.AppId(plan.Project, plan.Environment, plan.AppName);

        var placeIsGuaranteed = await EnsurePlaceAsync(plan, ct);

        var accepted = await DeployAsync(plan, appId, placeIsGuaranteed, ct);
        logger.LogInformation(
            "Caravana accepted the deploy of app '{AppId}' (deployment {Id})",
            appId,
            accepted.Deployment.Id
        );

        if (!plan.Wait)
        {
            LogWarnings(accepted.Deployment);
            return accepted.Deployment;
        }

        var waitTime = Stopwatch.StartNew();
        var waiter = new CaravanaDeploymentWaiter(api, logger, plan.PollInterval);
        var outcome = await waiter.WaitAsync(accepted.Location, accepted.Deployment, plan.Timeout, ct);
        var final = outcome.Deployment ?? accepted.Deployment;
        LogWarnings(final);

        switch (outcome.Kind)
        {
            case DeploymentOutcomeKind.Healthy:
                logger.LogInformation("App '{AppId}' is healthy (deployment {Id})", appId, final.Id);
                return final;

            case DeploymentOutcomeKind.TimedOut:
                throw new CaravanaDeployException(
                    $"Deployment {final.Id} of app '{appId}' was still {final.Status} after "
                        + $"{plan.Timeout.TotalMinutes:0.#} minutes. It may still finish: check it in Caravana."
                );

            default:
                var reason = string.IsNullOrWhiteSpace(final.Error) ? "no reason given" : redactor.Redact(final.Error);
                var failure = $"Deployment {final.Id} of app '{appId}' {final.Status}: {reason}";

                if (outcome.Kind != DeploymentOutcomeKind.Superseded)
                    throw new CaravanaDeployException(failure);

                return await FollowSupersededAsync(plan, appId, final, failure, waiter, waitTime, ct);
        }
    }

    /// <summary>
    /// Caravana supersedes a deployment as soon as the newer one is registered, so the newer one is usually
    /// still in progress: wait for it (and for what supersedes it in turn, a few hops at most) within the time
    /// the step has left, then judge it. The step passes, with a warning, only when the newest is healthy.
    /// </summary>
    private async Task<DeploymentInfo> FollowSupersededAsync(
        DeployPlan plan,
        string appId,
        DeploymentInfo superseded,
        string failure,
        CaravanaDeploymentWaiter waiter,
        Stopwatch waitTime,
        CancellationToken ct
    )
    {
        var current = superseded;
        for (var hops = 0; ; hops++)
        {
            var (newer, unread) = await FindNewerAsync(appId, current, ct);
            if (newer is null)
                throw new CaravanaDeployException($"{failure}. {unread}");
            current = newer;

            if (plan.FailOnSuperseded)
                throw new CaravanaDeployException($"{failure}. Superseded by {Describe(current)}, now {current.Status}.");

            if (!current.IsFinal)
            {
                try
                {
                    var location = CaravanaApiClient.DeploymentPath(appId, current.Id);
                    var outcome = await waiter.WaitAsync(location, current, plan.Timeout - waitTime.Elapsed, ct);
                    current = outcome.Deployment ?? current;
                    if (outcome.Kind == DeploymentOutcomeKind.TimedOut)
                        throw new CaravanaDeployException(
                            $"{failure}. Superseded by {Describe(current)}, still {current.Status} when the wait ended: "
                                + "check the app in Caravana."
                        );
                }
                catch (Exception ex) when (ex is CaravanaApiException or HttpRequestException)
                {
                    var text = ex is CaravanaApiException apiError ? apiError.Describe() : ex.Message;
                    throw new CaravanaDeployException(
                        $"{failure}. The newer deployment could not be read ({redactor.Redact(text)}): check the app in Caravana."
                    );
                }
            }

            if (current.Status == "superseded" && hops < MaxSupersedeHops)
                continue; // something newer replaced it in turn: follow the chain to the newest

            if (current.Status != "healthy")
                throw new CaravanaDeployException(
                    $"{failure}. Superseded by {Describe(current)}, now {current.Status}: the app is not healthy."
                );

            logger.LogWarning(
                "Deployment {Id} of app '{AppId}' was superseded by {By}, now healthy",
                superseded.Id,
                appId,
                Describe(current)
            );
            return superseded;
        }
    }

    /// <summary>
    /// The deployment that replaced <paramref name="superseded"/>: the app's newest one. Either it, or why it
    /// could not be found.
    /// </summary>
    private async Task<(DeploymentInfo? Newer, string Problem)> FindNewerAsync(
        string appId,
        DeploymentInfo superseded,
        CancellationToken ct
    )
    {
        try
        {
            var deployments = await api.ListDeploymentsAsync(appId, 20, ct);
            var newest = deployments.FirstOrDefault();
            return newest is null || newest.Id == superseded.Id
                ? (null, "No newer deployment was found to say what replaced it: check the app in Caravana.")
                : (newest, "");
        }
        catch (Exception ex) when (ex is CaravanaApiException or HttpRequestException)
        {
            var text = ex is CaravanaApiException apiError ? apiError.Describe() : ex.Message;
            return (null, $"The newer deployment could not be read ({redactor.Redact(text)}): check the app in Caravana.");
        }
    }

    /// <summary><c>deployment d2 (v3, from the console)</c>: which deployment it is and where it came from.</summary>
    private string Describe(DeploymentInfo deployment)
    {
        var details = new List<string>();
        if (deployment.JobVersion is { } version)
            details.Add($"v{version}");
        details.Add(Source(deployment));
        return redactor.Redact($"deployment {deployment.Id} ({string.Join(", ", details)})");
    }

    private static string Source(DeploymentInfo deployment)
    {
        // Origin is what CI sent in the Caravana-Origin-* headers; it wins over the trigger.
        if (deployment.Origin is { } origin)
        {
            var commit = origin.Commit is { Length: > 0 } c ? c[..Math.Min(7, c.Length)] : null;
            return commit is null ? "from CI" : $"from CI {commit}";
        }

        return deployment.Trigger switch
        {
            "console" => "from the console",
            "manual" => "by the API",
            "webhook" => "by a webhook",
            { Length: > 0 } other => $"trigger {other}",
            _ => "origin unknown",
        };
    }

    /// <summary>
    /// Creates the project and environment when they are missing. A deploy token can't even read projects
    /// (403): then they are assumed to exist, and the answer to the deploy says if they don't.
    /// </summary>
    /// <returns>True when the project and environment are known to exist.</returns>
    private async Task<bool> EnsurePlaceAsync(DeployPlan plan, CancellationToken ct)
    {
        if (plan.Project == CaravanaPlace.DefaultProject && plan.Environment == CaravanaPlace.DefaultEnvironment)
            return true; // every installation has them

        try
        {
            var project = await api.GetProjectAsync(plan.Project, ct);
            if (project is null)
            {
                logger.LogInformation("Creating project '{Project}'", plan.Project);
                await api.CreateProjectAsync(plan.Project, null, ct);
                // A new project starts with `production`.
                if (plan.Environment != CaravanaPlace.DefaultEnvironment)
                {
                    logger.LogInformation("Creating environment '{Environment}'", plan.Environment);
                    await api.CreateEnvironmentAsync(plan.Project, plan.Environment, ct);
                }
            }
            else if (!project.Environments.Contains(plan.Environment))
            {
                logger.LogInformation("Creating environment '{Environment}' in project '{Project}'", plan.Environment, plan.Project);
                await api.CreateEnvironmentAsync(plan.Project, plan.Environment, ct);
            }
            return true;
        }
        catch (CaravanaApiException ex) when (ex.Status == 403)
        {
            logger.LogInformation(
                "The token can't read or create projects (a deploy token?). Assuming project '{Project}' and "
                    + "environment '{Environment}' exist.",
                plan.Project,
                plan.Environment
            );
            return false;
        }
        catch (CaravanaApiException ex)
        {
            throw new CaravanaDeployException($"Could not prepare project '{plan.Project}' and environment '{plan.Environment}': {ex.Describe()}");
        }
    }

    private async Task<AcceptedDeployment> DeployAsync(DeployPlan plan, string appId, bool placeIsGuaranteed, CancellationToken ct)
    {
        // Two tries: the app can change between the read and the write (another deploy, the console),
        // and then the revision we hold is stale. One re-read settles it; a second conflict is real.
        for (var attempt = 0; ; attempt++)
        {
            AppInfo? app;
            try
            {
                app = await api.GetAppAsync(appId, ct);
            }
            catch (CaravanaApiException ex)
            {
                throw new CaravanaDeployException($"Could not read app '{appId}': {ex.Describe()}");
            }

            logger.LogInformation(
                app is null ? "Creating app '{AppId}'" : "Updating app '{AppId}'",
                appId
            );

            try
            {
                return await api.DeployComposeAsync(appId, plan.Body, app?.Revision, plan.OriginHeaders, ct);
            }
            catch (CaravanaApiException ex) when (attempt == 0 && ex.Code is "app_changed" or "app_exists")
            {
                logger.LogInformation("The app changed while deploying ({Code}); reading it again", ex.Code);
            }
            catch (CaravanaApiException ex)
            {
                throw new CaravanaDeployException(Explain(ex, plan, placeIsGuaranteed));
            }
        }
    }

    private static string Explain(CaravanaApiException ex, DeployPlan plan, bool placeIsGuaranteed)
    {
        var text = ex.Describe();
        switch (ex.Code)
        {
            case "project_not_found" or "environment_not_found" when !placeIsGuaranteed:
                return $"{text}. This token can't create projects or environments (it looks like a deploy token): "
                    + $"create project '{plan.Project}' and environment '{plan.Environment}' in Caravana first, "
                    + "or deploy with a full API token.";
            case "image_not_found" or "registry_auth_failed":
                return $"{text}. If the image is private, Caravana needs credentials for that registry "
                    + "(Settings > Registries).";
            case "no_backup_destination":
                return $"{text}. A service has a backup policy, but Caravana has no backup destination "
                    + "(Settings > Backup destination).";
            default:
                return text;
        }
    }

    private void LogWarnings(DeploymentInfo deployment)
    {
        foreach (var warning in deployment.Warnings)
        {
            var where = string.IsNullOrEmpty(warning.Field) ? "" : $" ({redactor.Redact(warning.Field)})";
            logger.LogWarning("Caravana warning{Where}: {Message}", where, redactor.Redact(warning.Message));
        }
    }
}
