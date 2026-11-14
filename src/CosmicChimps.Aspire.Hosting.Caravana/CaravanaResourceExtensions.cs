using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using System.Text.RegularExpressions;
using Aspire.Hosting.Pipelines;

namespace CosmicChimps.Aspire.Hosting.Caravana;

/// <summary>Publishing an Aspire application to Caravana.</summary>
public static partial class CaravanaResourceExtensions
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
            FailOnSuperseded = settings.FailOnSuperseded,
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

    /// <summary>
    /// Makes Caravana restore, on a schedule, the service's backup pinned under a label (for instance to
    /// reset a demo every night to a configured state). Pin the backup once in Caravana (the Backups tab, or
    /// <c>PUT /v1/apps/{id}/backups/{backupId}/pin</c>); until one is pinned, the deploy warns and each
    /// scheduled restore is refused and shown on Caravana's needs-you list. Needs
    /// <see cref="WithCaravanaBackup{T}"/> on the same service.
    /// </summary>
    /// <param name="from">The label of the pinned backup: lowercase letters, digits and '-', 1 to 32, such as <c>golden</c>.</param>
    /// <param name="schedule">Five-field cron in UTC, such as <c>0 4 * * *</c>.</param>
    public static IResourceBuilder<T> WithCaravanaScheduledRestore<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<CaravanaResource> caravana,
        string from,
        string schedule
    )
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(caravana);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(schedule);
        if (!LabelPattern().IsMatch(from))
            throw new ArgumentException(
                "A label is 1 to 32 lowercase letters, digits and '-', starting and ending with a letter or digit.",
                nameof(from)
            );
        schedule = schedule.Trim();
        if (schedule.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 5)
            throw new ArgumentException("The schedule is a five-field cron expression in UTC, such as 0 4 * * *.", nameof(schedule));
        caravana.Resource.Annotations.Add(
            new CaravanaRestoreAnnotation { ServiceName = ServiceName(resourceBuilder), From = from, Schedule = schedule }
        );
        return resourceBuilder;
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,30}[a-z0-9])?$")]
    private static partial Regex LabelPattern();

    /// <summary>The identities Caravana allows on one service.</summary>
    internal const int MaxIdentities = 4;

    /// <summary>
    /// Asks Caravana for a Nomad workload identity for each copy of the service: Nomad writes a signed
    /// token for the audience to <c>/secrets/nomad_{name}.jwt</c> in the copy and renews it, so the service
    /// can prove who it is to another system (a secrets service, for instance) without a stored secret.
    /// The publisher never reads or stores the token. Call it again for another identity (at most 4 per
    /// service, each with its own name). Needs a Caravana with workload identities (API 0.41.0; an older
    /// one refuses the deploy, naming the unknown field).
    /// </summary>
    /// <param name="name">Lowercase letters, digits and '_', starting with a letter, 1 to 30, such as <c>bella</c>. Not <c>default</c>, and not starting with <c>vault_</c> or <c>consul_</c>.</param>
    /// <param name="audience">What the token is for: 1 to 4 values of 1 to 255 printable ASCII characters, no spaces.</param>
    /// <param name="ttl">How long a token lives before Nomad renews it: whole seconds, 1 minute to 24 hours. Leave it out for Caravana's default (1 hour).</param>
    public static IResourceBuilder<T> WithCaravanaIdentity<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<CaravanaResource> caravana,
        string name,
        IEnumerable<string> audience,
        TimeSpan? ttl = null
    )
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(caravana);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(audience);
        if (!IdentityNamePattern().IsMatch(name))
            throw new ArgumentException(
                "An identity name is 1 to 30 lowercase letters, digits and '_', starting with a letter.",
                nameof(name)
            );
        if (name == "default" || name.StartsWith("vault_", StringComparison.Ordinal) || name.StartsWith("consul_", StringComparison.Ordinal))
            throw new ArgumentException(
                "An identity name can't be 'default' or start with 'vault_' or 'consul_': Nomad uses those.",
                nameof(name)
            );
        var values = audience.ToList();
        if (values.Count is < 1 or > 4)
            throw new ArgumentException("An identity has 1 to 4 audience values.", nameof(audience));
        foreach (var value in values)
            CheckAudienceValue(value, nameof(audience));
        int? ttlSeconds = null;
        if (ttl is { } t)
        {
            if (t.Ticks % TimeSpan.TicksPerSecond != 0)
                throw new ArgumentException("The ttl is a whole number of seconds.", nameof(ttl));
            if (t < TimeSpan.FromSeconds(60) || t > TimeSpan.FromSeconds(86400))
                throw new ArgumentOutOfRangeException(nameof(ttl), "The ttl is between 1 minute and 24 hours.");
            ttlSeconds = (int)t.TotalSeconds;
        }
        caravana.Resource.Annotations.Add(
            new CaravanaIdentityAnnotation
            {
                ServiceName = ServiceName(resourceBuilder),
                Name = name,
                Audience = values,
                TtlSeconds = ttlSeconds,
            }
        );
        return resourceBuilder;
    }

    /// <summary>
    /// Shorthand for <see cref="WithCaravanaIdentity{T}"/> with the identity Bella Baxter asks for: the name
    /// <c>bella</c> and the audience <c>bella-spiffe/{environmentId}</c>, so the service finds its token in
    /// <c>/secrets/nomad_bella.jwt</c>.
    /// </summary>
    /// <param name="environmentId">The id of the Bella environment, as its page recommends the audience for.</param>
    /// <param name="ttl">As for <see cref="WithCaravanaIdentity{T}"/>.</param>
    public static IResourceBuilder<T> WithCaravanaBellaIdentity<T>(
        this IResourceBuilder<T> resourceBuilder,
        IResourceBuilder<CaravanaResource> caravana,
        string environmentId,
        TimeSpan? ttl = null
    )
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(environmentId);
        var audience = "bella-spiffe/" + environmentId;
        try
        {
            CheckAudienceValue(audience, nameof(environmentId));
        }
        catch (ArgumentException)
        {
            throw new ArgumentException(
                "The environment id must make a valid audience: bella-spiffe/ and the id together are 1 to 255 printable ASCII characters, no spaces.",
                nameof(environmentId)
            );
        }
        return resourceBuilder.WithCaravanaIdentity(caravana, "bella", [audience], ttl);
    }

    private static void CheckAudienceValue(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (value.Length is < 1 or > 255 || value.Any(c => c < '!' || c > '~'))
            throw new ArgumentException(
                "An audience value is 1 to 255 printable ASCII characters, no spaces.",
                paramName
            );
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,29}$")]
    private static partial Regex IdentityNamePattern();

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
