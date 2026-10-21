using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using CosmicChimps.Aspire.Hosting.Caravana.Models;
using Microsoft.Extensions.Logging;

namespace CosmicChimps.Aspire.Hosting.Caravana;

/// <summary>The deploy step: resolve, read what Aspire wrote, transform, deploy, wait.</summary>
internal static class CaravanaDeployer
{
    internal static async Task RunStepAsync(CaravanaResource resource, PipelineStepContext ctx)
    {
        var logger = ctx.Logger;
        var ct = ctx.CancellationToken;

        // Resolve everything first, then validate: the resolved properties are empty until this
        // runs, so validating first fails every deploy with "not set" for settings that are fine.
        await resource.ResolveConfigurationAsync(ct);
        Validate(resource, logger);

        var redactor = new SecretRedactor();
        redactor.Add(resource.Token);

        var composePath = FindComposePath(resource, ctx);
        var composeYaml = await File.ReadAllTextAsync(composePath, ct);
        var (envPath, envValues) = EnvFile.LoadFirst(Path.GetDirectoryName(composePath)!);
        logger.LogInformation(
            "Read {Compose} and {Env} ({Count} variables)",
            composePath,
            envPath ?? "no .env file",
            envValues.Count
        );

        var secrets = await ResolveSecretsAsync(ctx.Model.Resources, envValues, ct);
        redactor.Add(secrets.Values);

        var settings = await CollectSettingsAsync(resource, ct);

        var transformed = CaravanaComposeTransformer.Transform(
            new TransformInput(composeYaml, envValues, secrets, resource.DeployDashboard, settings)
        );
        foreach (var warning in transformed.Warnings)
            logger.LogWarning("{Warning}", redactor.Redact(warning));
        if (!transformed.Success)
            throw new CaravanaDeployException(
                "The Compose file can't be sent to Caravana:" + string.Concat(transformed.Errors.Select(e => "\n  - " + redactor.Redact(e)))
            );

        var request = new ComposeRequest(
            transformed.Compose!,
            string.IsNullOrWhiteSpace(resource.DisplayName) ? null : resource.DisplayName,
            transformed.Services
        );
        var plan = new DeployPlan(
            resource.Project,
            resource.Environment,
            resource.AppName,
            request,
            resource.WaitForDeployment,
            resource.DeploymentTimeout,
            resource.PollInterval,
            OriginHeaders.From(Environment.GetEnvironmentVariable),
            resource.FailOnSuperseded
        );

        logger.LogInformation(
            "Deploying '{App}' to Caravana ({Services} services, {Secrets} secrets)",
            CaravanaPlace.AppId(plan.Project, plan.Environment, plan.AppName),
            transformed.Services.Count,
            transformed.Services.Values.Sum(s => s.Secrets?.Count ?? 0)
        );

        using var http = new HttpClient { BaseAddress = new Uri(resource.Url.TrimEnd('/') + "/") };
        var api = new CaravanaApiClient(http, resource.Token, redactor);

        await using var task = await ctx.ReportingStep.CreateTaskAsync($"Deploying to Caravana ({resource.Url})", ct);
        try
        {
            var deployment = await new CaravanaDeployFlow(api, redactor, logger).RunAsync(plan, ct);
            await task.CompleteAsync(
                $"Deployment {deployment.Id}: {deployment.Status}",
                deployment.Warnings.Count > 0 || deployment.Status == "superseded"
                    ? CompletionState.CompletedWithWarning
                    : CompletionState.Completed,
                ct
            );
        }
        catch (Exception ex) when (ex is CaravanaDeployException or HttpRequestException or TaskCanceledException)
        {
            await task.CompleteAsync(redactor.Redact(ex.Message), CompletionState.CompletedWithError, ct);
            throw ex is CaravanaDeployException ? ex : new CaravanaDeployException($"Could not reach Caravana: {redactor.Redact(ex.Message)}");
        }
    }

    private static void Validate(CaravanaResource resource, ILogger logger)
    {
        var problems = new List<string>(CaravanaPlace.Validate(resource.Project, resource.Environment, resource.AppName));

        if (!Uri.TryCreate(resource.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            problems.Add("Url must be an absolute http(s) address such as https://caravana.example.com.");
        else if (url.Scheme == "http" && !url.IsLoopback)
            logger.LogWarning("Caravana's address is plain http: the token travels unencrypted.");

        if (problems.Count > 0)
            throw new CaravanaDeployException("Invalid Caravana settings:" + string.Concat(problems.Select(p => "\n  - " + p)));
    }

    private static string FindComposePath(CaravanaResource resource, PipelineStepContext ctx)
    {
        var candidates = new List<string>();

        // Where Aspire says it wrote the Compose environment's output, when it can say.
        try
        {
            var output = ctx.Services.GetService(typeof(IPipelineOutputService)) as IPipelineOutputService;
            if (output is not null)
                candidates.Add(Path.Combine(output.GetOutputDirectory(resource.ComposeEnvironment), "docker-compose.yaml"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Logger.LogDebug("The pipeline could not name the output directory: {Error}", ex.Message);
        }

        var root = Path.Combine(Directory.GetCurrentDirectory(), "aspire-output");
        candidates.Add(Path.Combine(root, resource.ComposeEnvironment.Name, "docker-compose.yaml"));
        candidates.Add(Path.Combine(root, "docker-compose.yaml"));
        candidates.Add(Path.Combine(root, "docker-compose.yml"));

        foreach (var path in candidates)
        {
            ctx.Logger.LogDebug("Looking for the Compose file at {Path}", path);
            if (File.Exists(path))
                return path;
        }

        throw new CaravanaDeployException(
            "Could not find docker-compose.yaml. Looked at:" + string.Concat(candidates.Select(c => "\n  " + c))
        );
    }

    /// <summary>
    /// The secret parameters, by the name Aspire gives them in the .env file (<c>cache-password</c>
    /// becomes <c>CACHE_PASSWORD</c>), with their values: the .env file's, else the parameter's own.
    /// </summary>
    internal static async Task<Dictionary<string, string>> ResolveSecretsAsync(
        IEnumerable<IResource> resources,
        IReadOnlyDictionary<string, string> envValues,
        CancellationToken ct
    )
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in resources.OfType<ParameterResource>().Where(p => p.Secret))
        {
            var key = EnvName(parameter.Name);
            if (envValues.TryGetValue(key, out var fromEnv) && fromEnv.Length > 0)
            {
                secrets[key] = fromEnv;
                continue;
            }

            try
            {
                var value = await parameter.GetValueAsync(ct);
                if (!string.IsNullOrEmpty(value))
                    secrets[key] = value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A secret nobody can read isn't in the file either, or the missing-variable check says so.
            }
        }
        return secrets;
    }

    internal static string EnvName(string parameterName) => parameterName.ToUpperInvariant().Replace('-', '_');

    /// <summary>The per-resource settings, with domain hosts resolved.</summary>
    internal static async Task<Dictionary<string, CaravanaServiceSettings>> CollectSettingsAsync(
        CaravanaResource resource,
        CancellationToken ct
    )
    {
        var settings = new Dictionary<string, CaravanaServiceSettings>(StringComparer.Ordinal);
        CaravanaServiceSettings For(string name) =>
            settings.TryGetValue(name, out var s) ? s : settings[name] = new CaravanaServiceSettings();

        foreach (var annotation in resource.Annotations.OfType<CaravanaServiceAnnotation>())
        {
            var s = For(annotation.ServiceName);
            switch (annotation)
            {
                case CaravanaDomainAnnotation d:
                    var host = (await d.Host.GetValueAsync(ct))?.Trim();
                    if (string.IsNullOrEmpty(host))
                        throw new CaravanaDeployException(
                            $"The domain of '{d.ServiceName}' ({d.Host}) resolved to an empty value."
                        );
                    if (host.Contains('/') || host.Contains(':') || host.Contains(' '))
                        throw new CaravanaDeployException(
                            $"The domain of '{d.ServiceName}' ({d.Host}) must be a host name such as shop.example.com, not an address."
                        );
                    s.Domains.Add((host.ToLowerInvariant(), d.Port));
                    break;
                case CaravanaHealthAnnotation h:
                    s.HasHealth = true;
                    s.HealthPath = h.Path;
                    s.HealthPort = h.Port;
                    break;
                case CaravanaBackupAnnotation b:
                    s.BackupEngine = b.Engine;
                    s.BackupSchedule = b.Schedule;
                    s.BackupKeep = b.Keep;
                    break;
                case CaravanaMemoryAnnotation m:
                    s.ReservedMB = m.ReservedMB;
                    s.MaxMB = m.MaxMB;
                    break;
                case CaravanaExcludeAnnotation:
                    s.Exclude = true;
                    break;
            }
        }
        return settings;
    }
}
