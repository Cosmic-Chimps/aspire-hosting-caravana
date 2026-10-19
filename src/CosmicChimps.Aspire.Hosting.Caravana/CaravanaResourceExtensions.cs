using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace CosmicChimps.Aspire.Hosting.Caravana;

/// <summary>Publishing an Aspire application to Caravana.</summary>
public static class CaravanaResourceExtensions
{
    /// <summary>
    /// Makes <c>aspire deploy</c> publish the AppHost to Caravana as one app: each resource a
    /// service, after Aspire has built and pushed the images.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">Names the Caravana resource and the Compose environment; the app's name unless
    /// <see cref="CaravanaSettings.AppName"/> says otherwise.</param>
    /// <param name="configure">Sets <see cref="CaravanaSettings"/>.</param>
    /// <example>
    /// <code>
    /// builder.PublishToCaravana("shop", s =>
    /// {
    ///     s.Url = caravanaUrl.AsCaravanaValue();
    ///     s.Token = caravanaToken.AsCaravanaValue();
    ///     s.Project = "shop";
    ///     s.Environment = "production";
    /// });
    /// </code>
    /// </example>
    public static IResourceBuilder<CaravanaResource> PublishToCaravana(
        this IDistributedApplicationBuilder builder,
        string name,
        Action<CaravanaSettings>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var settings = new CaravanaSettings();
        configure?.Invoke(settings);

        var composeEnv = builder.AddDockerComposeEnvironment($"{name}-compose");

        var resource = new CaravanaResource(name)
        {
            // Deferred: stored, not read. A setting may be an Aspire parameter, which can't be
            // resolved while the model is being built. ResolveConfigurationAsync does it when the
            // deploy step runs, defaults included.
            UrlSource = settings.Url,
            TokenSource = settings.Token,
            ProjectSource = settings.Project,
            EnvironmentSource = settings.Environment,
            AppNameSource = settings.AppName,
            DisplayNameSource = settings.DisplayName,
            DefaultAppName = name,
            ComposeEnvironment = composeEnv.Resource,
            ComposeEnvironmentBuilder = composeEnv,
            DeployDashboard = settings.DeployDashboard,
            WaitForDeployment = settings.WaitForDeployment,
            DeploymentTimeout = settings.DeploymentTimeout,
        };

        // The step runs after the Compose file is written (publish) and the images are built and
        // pushed. DistributedApplicationPipeline scans every resource for a PipelineStepAnnotation.
        resource.Annotations.Add(
            new PipelineStepAnnotation(_ =>
            {
                var step = new PipelineStep
                {
                    Name = $"caravana-deploy-{name}",
                    Description = $"Deploys Aspire app '{name}' to Caravana.",
                    Action = async ctx => await CaravanaDeployer.RunStepAsync(resource, ctx),
                };

                // The Compose file is written in the "publish" stage; images are built in "build".
                step.DependsOn("publish");
                step.DependsOn("build");

                // ...and PUSHED. Each push-{resource} step is required by the "push" meta-step,
                // not by "build"; waiting on build alone names a tag the registry may not hold yet.
                step.DependsOn(WellKnownPipelineSteps.Push);

                // ...and the ${VAR} image placeholders in the Compose file are resolved into the
                // .env file by the Compose environment's own prepare step. Without this dependency
                // the step races it and reads unresolved images.
                step.DependsOn($"prepare-{name}-compose");

                // Take part in the standard "deploy" aggregate step.
                step.RequiredBy("deploy");

                return step;
            })
        );

        // The Compose environment always registers a docker-compose-up step. With Caravana as the
        // target there is no local daemon to run it against, so its action becomes a no-op.
        // PipelineStep.Action has an init accessor (enforced by the compiler only), hence reflection.
        resource.Annotations.Add(
            new PipelineConfigurationAnnotation(ctx =>
            {
                var upStep = ctx.Steps.FirstOrDefault(s => s.Name == $"docker-compose-up-{name}-compose");
                if (upStep is not null)
                {
                    typeof(PipelineStep)
                        .GetProperty(nameof(PipelineStep.Action))!
                        .SetValue(upStep, (Func<PipelineStepContext, Task>)(_ => Task.CompletedTask));
                }

                return Task.CompletedTask;
            })
        );

        return builder.AddResource(resource);
    }

    /// <summary>Routes a public host to this resource over HTTPS.</summary>
    /// <param name="resourceBuilder">The resource (a project, a container).</param>
    /// <param name="caravana">The Caravana resource returned by <see cref="PublishToCaravana"/>.</param>
    /// <param name="host">The public host name, e.g. <c>shop.example.com</c>.</param>
    /// <param name="port">The container port to route to. Leave it out when the service has one port.</param>
    public static IResourceBuilder<T> WithCaravanaDomain<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<CaravanaResource> caravana,
        string host,
        int? port = null
    )
        where T : IResource
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return resourceBuilder.WithCaravanaDomainCore(caravana, CaravanaValue.FromLiteral(host.Trim()), port);
    }

    /// <summary>Routes a public host, taken from an Aspire parameter, to this resource over HTTPS.</summary>
    public static IResourceBuilder<T> WithCaravanaDomain<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<CaravanaResource> caravana,
        IResourceBuilder<ParameterResource> host,
        int? port = null
    )
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(host);
        return resourceBuilder.WithCaravanaDomainCore(caravana, CaravanaValue.FromParameter(host), port);
    }

    private static IResourceBuilder<T> WithCaravanaDomainCore<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<CaravanaResource> caravana,
        CaravanaValue host,
        int? port
    )
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(caravana);
        CheckPort(port, nameof(port));
        caravana.Resource.Annotations.Add(
            new CaravanaDomainAnnotation { ServiceName = ServiceName(resourceBuilder), Host = host, Port = port }
        );
        return resourceBuilder;
    }

    /// <summary>
    /// The check Nomad runs. Without it a service with ports is checked over TCP on its first port.
    /// </summary>
    /// <param name="path">Makes it an HTTP check on this path, e.g. <c>/health</c>. Leave it out for TCP.</param>
    /// <param name="port">The container port to check. Leave it out when the service has one port.</param>
    public static IResourceBuilder<T> WithCaravanaHealthCheck<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<CaravanaResource> caravana,
        string? path = null,
        int? port = null
    )
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(caravana);
        CheckPort(port, nameof(port));
        if (path is not null && !path.StartsWith('/'))
            throw new ArgumentException("A health check path starts with '/'.", nameof(path));
        caravana.Resource.Annotations.Add(
            new CaravanaHealthAnnotation { ServiceName = ServiceName(resourceBuilder), Path = path, Port = port }
        );
        return resourceBuilder;
    }

    /// <summary>
    /// Makes Caravana back the service up to the installation's backup destination. Caravana refuses
    /// the deploy, naming the field, if no destination is set.
    /// </summary>
    /// <param name="engine">Postgres dump, or volume archive.</param>
    /// <param name="schedule">Five-field cron in UTC. Leave it out for Caravana's default (<c>0 3 * * *</c>).</param>
    /// <param name="keep">How many backups stay (1 to 100). Leave it out for Caravana's default (7).</param>
    public static IResourceBuilder<T> WithCaravanaBackup<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<CaravanaResource> caravana,
        CaravanaBackupEngine engine,
        string? schedule = null,
        int? keep = null
    )
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(caravana);
        if (keep is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(keep), "keep is between 1 and 100.");
        caravana.Resource.Annotations.Add(
            new CaravanaBackupAnnotation
            {
                ServiceName = ServiceName(resourceBuilder),
                Engine = engine,
                Schedule = string.IsNullOrWhiteSpace(schedule) ? null : schedule.Trim(),
                Keep = keep,
            }
        );
        return resourceBuilder;
    }

    /// <summary>Reserves memory for the service and optionally lets it burst to a ceiling (in MB).</summary>
    /// <remarks>Written to the Compose file as <c>mem_reservation</c> and <c>mem_limit</c>.</remarks>
    public static IResourceBuilder<T> WithCaravanaMemory<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<CaravanaResource> caravana,
        int reservedMB,
        int? maxMB = null
    )
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(caravana);
        if (reservedMB < 1)
            throw new ArgumentOutOfRangeException(nameof(reservedMB), "The reservation is at least 1 MB.");
        if (maxMB is not null && maxMB < reservedMB)
            throw new ArgumentOutOfRangeException(nameof(maxMB), "The ceiling can't be below the reservation.");
        caravana.Resource.Annotations.Add(
            new CaravanaMemoryAnnotation
            {
                ServiceName = ServiceName(resourceBuilder),
                ReservedMB = reservedMB,
                MaxMB = maxMB,
            }
        );
        return resourceBuilder;
    }

    /// <summary>Leaves this resource out of the Caravana app.</summary>
    public static IResourceBuilder<T> WithCaravanaExclude<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<CaravanaResource> caravana
    )
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(caravana);
        caravana.Resource.Annotations.Add(new CaravanaExcludeAnnotation { ServiceName = ServiceName(resourceBuilder) });
        return resourceBuilder;
    }

    // Aspire names the Compose service after the resource, in lower case.
    private static string ServiceName<T>(IResourceBuilder<T> resourceBuilder)
        where T : IResource => resourceBuilder.Resource.Name.ToLowerInvariant();

    private static void CheckPort(int? port, string paramName)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(paramName, "A port is between 1 and 65535.");
    }
}
