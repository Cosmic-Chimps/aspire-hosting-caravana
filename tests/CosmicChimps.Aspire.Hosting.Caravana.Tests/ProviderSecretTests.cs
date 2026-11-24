using System.Net;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using CosmicChimps.Aspire.Hosting.Caravana.Models;
using Xunit;

namespace CosmicChimps.Aspire.Hosting.Caravana.Tests;

/// <summary>
/// <c>AsCaravanaProviderSecret</c>: its checks, the stand-in value, the version gate and the whole request.
/// </summary>
public class ProviderSecretTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private const string RealValue = "real-db-password-from-config";
    private const string Token = "cvat_token-test-0002";

    private static IDistributedApplicationBuilder Publish() =>
        DistributedApplication.CreateBuilder(["--operation", "publish"]);

    // ── the extension ────────────────────────────────────────────────────────

    [Fact]
    public void ABadProviderOrPath_IsRefused_NamingTheArgument()
    {
        var parameter = Publish().AddParameter("db-password", secret: true);

        foreach (var provider in new[] { "", "Bella", "bella_prod", "-bella", "bella-", "a b", new string('a', 64), "bella.prod", "bélla" })
            Assert.Equal("provider", Assert.Throws<ArgumentException>(() => parameter.AsCaravanaProviderSecret(provider, "X")).ParamName);
        foreach (var path in new[] { "", new string('a', 513), "a\nb", "a\tb", "a\u007fb", "a\u0085b", "a\0b" })
            Assert.Equal("path", Assert.Throws<ArgumentException>(() => parameter.AsCaravanaProviderSecret("bella-prod", path)).ParamName);
        Assert.Throws<ArgumentNullException>(() => parameter.AsCaravanaProviderSecret(null!, "X"));
        Assert.Throws<ArgumentNullException>(() => parameter.AsCaravanaProviderSecret("bella-prod", null!));

        // The limits themselves are fine, and so are the characters Bella's paths use, '-' and '/' among them.
        parameter.AsCaravanaProviderSecret(new string('a', 63), new string('a', 512));
    }

    [Fact]
    public void ThePathIsGivenAsItIs_NeverTakenFromTheParametersName()
    {
        var builder = Publish();
        var parameter = builder.AddParameter("db-password", secret: true).AsCaravanaProviderSecret("bella-prod", "banana/DB_PASSWORD");

        var annotation = Assert.Single(parameter.Resource.Annotations.OfType<CaravanaProviderSecretAnnotation>());
        Assert.Equal("bella-prod", annotation.Provider);
        Assert.Equal("banana/DB_PASSWORD", annotation.Path);
    }

    [Fact]
    public void ItReturnsTheSameBuilder_AndMarkingTwiceIsAllowedOnlyForTheSameSecret()
    {
        var parameter = Publish().AddParameter("db-password", secret: true);

        var marked = parameter.AsCaravanaProviderSecret("bella-prod", "DB_PASSWORD");
        var again = marked.AsCaravanaProviderSecret("bella-prod", "DB_PASSWORD");

        Assert.Same(parameter, marked);
        Assert.Same(parameter, again);
        Assert.Single(parameter.Resource.Annotations.OfType<CaravanaProviderSecretAnnotation>());
        Assert.Throws<InvalidOperationException>(() => parameter.AsCaravanaProviderSecret("bella-prod", "OTHER"));
    }

    [Fact]
    public async Task WhilePublishing_TheParameterHasAPerRunStandInNoMatterWhatConfigurationSays()
    {
        var builder = Publish();
        builder.Configuration["Parameters:db-password"] = RealValue;
        var parameter = builder.AddParameter("db-password", secret: true).AsCaravanaProviderSecret("bella-prod", "DB_PASSWORD");

        var value = await parameter.Resource.GetValueAsync(Ct);

        Assert.NotEqual(RealValue, value);
        Assert.Matches("^caravana-ref-[0-9a-f]{32}$", value);
        Assert.Equal(value, Assert.Single(parameter.Resource.Annotations.OfType<CaravanaProviderSecretAnnotation>()).Sentinel);

        // Per run: another parameter, another stand-in.
        var other = builder.AddParameter("jwt-key", secret: true).AsCaravanaProviderSecret("bella-prod", "JWT_KEY");
        Assert.NotEqual(value, await other.Resource.GetValueAsync(Ct));
    }

    [Fact]
    public async Task AParameterWithNoConfiguredValue_NeverMissesOne_SoAspireHasNothingToAskFor()
    {
        // Aspire prompts when reading the value throws MissingParameterValueException.
        var parameter = Publish().AddParameter("db-password", secret: true).AsCaravanaProviderSecret("bella-prod", "DB_PASSWORD");

        Assert.NotNull(await parameter.Resource.GetValueAsync(Ct));
    }

    [Fact]
    public async Task AParameterRead_BeforeItWasMarked_StillGivesTheStandIn()
    {
        var builder = Publish();
        builder.Configuration["Parameters:db-password"] = RealValue;
        var parameter = builder.AddParameter("db-password", secret: true);
        Assert.Equal(RealValue, await parameter.Resource.GetValueAsync(Ct)); // cached by Aspire

        parameter.AsCaravanaProviderSecret("bella-prod", "DB_PASSWORD");

        Assert.StartsWith("caravana-ref-", await parameter.Resource.GetValueAsync(Ct));
    }

    [Fact]
    public async Task WhenTheAppHostRunsLocally_TheParameterIsTheDevelopersOwn()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        builder.Configuration["Parameters:db-password"] = "local-dev-password";
        var parameter = builder.AddParameter("db-password", secret: true).AsCaravanaProviderSecret("bella-prod", "DB_PASSWORD");

        Assert.Equal("local-dev-password", await parameter.Resource.GetValueAsync(Ct));
    }

    [Fact]
    public async Task AProviderSecret_IsNotATypedSecret_AndItsStandInIsFoundByItsEnvName()
    {
        var builder = Publish();
        builder.Configuration["Parameters:cache-password"] = "typed-value";
        builder.Configuration["Parameters:db-password"] = RealValue;
        builder.AddParameter("cache-password", secret: true);
        builder.AddParameter("db-password", secret: true).AsCaravanaProviderSecret("bella-prod", "DB_PASSWORD");

        var typed = await CaravanaDeployer.ResolveSecretsAsync(builder.Resources, new Dictionary<string, string>(), Ct);
        var provider = Assert.Single(await CaravanaDeployer.ResolveProviderSecretsAsync(builder.Resources, Ct));

        Assert.Equal(["CACHE_PASSWORD"], typed.Keys);
        Assert.Equal("db-password", provider.ParameterName);
        Assert.Equal("bella-prod", provider.Provider);
        Assert.Equal("DB_PASSWORD", provider.Path);
        Assert.Equal("caravana-ref-", provider.Sentinel[..13]);
    }

    [Fact]
    public async Task IfAspireGivesTheParameterAnotherValue_TheDeployStopsNamingTheParameter()
    {
        // The marker was added while running locally; the value is a real one.
        var builder = DistributedApplication.CreateBuilder([]);
        builder.Configuration["Parameters:db-password"] = RealValue;
        builder.AddParameter("db-password", secret: true).AsCaravanaProviderSecret("bella-prod", "DB_PASSWORD");

        var ex = await Assert.ThrowsAsync<CaravanaDeployException>(() => CaravanaDeployer.ResolveProviderSecretsAsync(builder.Resources, Ct));

        Assert.Contains("'db-password'", ex.Message);
        Assert.DoesNotContain(RealValue, ex.Message);
    }

    // ── the whole request ────────────────────────────────────────────────────

    private static (CaravanaResource Resource, IDistributedApplicationBuilder Builder) AppHost()
    {
        var builder = Publish();
        builder.Configuration["Parameters:db-password"] = RealValue; // the developer's own value: must stay home
        builder.AddParameter("db-password", secret: true).AsCaravanaProviderSecret("bella-prod", "DB_PASSWORD");
        var caravana = builder.PublishToCaravana("shop");
        return (caravana.Resource, builder);
    }

    private static string BananaCompose() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "banana-docker-compose.yaml"));

    private static DeployPlan PlanOf(ComposeRequest request) =>
        new("default", "production", "shop", request, false, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1), new Dictionary<string, string>());

    private static FakeHandler Caravana(string? version, bool healthFails = false) =>
        new(r =>
            (r.Method.Method, r.Path) switch
            {
                ("GET", "/v1/health") when healthFails => FakeHandler.Error(HttpStatusCode.ServiceUnavailable, "unavailable", "down"),
                ("GET", "/v1/health") => FakeHandler.Json(HttpStatusCode.OK, version is null ? """{"status":"ok"}""" : $$"""{"status":"ok","version":"{{version}}"}"""),
                ("GET", "/v1/apps/shop") => FakeHandler.Error(HttpStatusCode.NotFound, "not_found", "no such app"),
                ("POST", "/v1/apps/shop/compose") => FakeHandler.Json(HttpStatusCode.Accepted, FakeHandler.Deployment("pending"), "/v1/apps/shop/deployments/d1"),
                _ => FakeHandler.Error(HttpStatusCode.NotFound, "not_found", r.ToString()),
            }
        );

    private static async Task<(ComposeRequest Request, FakeHandler Handler, ListLogger Log)> DeployBanana(string? serverVersion, bool healthFails = false)
    {
        var (resource, builder) = AppHost();
        var redactor = new SecretRedactor();
        redactor.Add(Token);
        var log = new ListLogger();
        // Aspire's .env, as it would be written: whatever the parameter's value was when it was published.
        var env = new Dictionary<string, string>
        {
            ["API_IMAGE"] = "ghcr.io/acme/api:1",
            ["DB_PASSWORD"] = RealValue,
        };
        var request = await CaravanaDeployer.BuildRequestAsync(resource, builder.Resources, BananaCompose(), env, log, redactor, Ct);

        var handler = Caravana(serverVersion, healthFails);
        var flow = new CaravanaDeployFlow(new CaravanaApiClient(handler.Client(), Token, redactor), redactor, log);
        await flow.RunAsync(PlanOf(request), Ct);
        return (request, handler, log);
    }

    [Fact]
    public async Task TheRealConfiguredValue_NeverAppearsInTheRequest_OrInTheLog()
    {
        var (request, handler, log) = await DeployBanana("0.99.0");

        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        foreach (var text in new[] { post.Body, JsonSerializer.Serialize(request, CaravanaJson.Options), log.All })
        {
            Assert.DoesNotContain(RealValue, text);
            Assert.DoesNotContain("caravana-ref-", text);
        }

        using var body = JsonDocument.Parse(post.Body);
        var keycloak = body.RootElement.GetProperty("services").GetProperty("keycloak").GetProperty("secrets")[0];
        Assert.Equal("KC_DB_PASSWORD", keycloak.GetProperty("name").GetString());
        Assert.Equal("DB_PASSWORD", keycloak.GetProperty("from").GetProperty("path").GetString());
        Assert.False(keycloak.TryGetProperty("value", out _));
        var api = body.RootElement.GetProperty("services").GetProperty("api").GetProperty("secrets")[0];
        Assert.Equal(
            "Host=postgres;Port=5432;Username=postgres;Password=${db_password};Database=banana",
            api.GetProperty("template").GetString()
        );
    }

    [Fact]
    public async Task AnOldCaravana_RefusesBeforeAnythingIsSent_OnlyTheHealthCheckGoesOut()
    {
        var (resource, builder) = AppHost();
        var redactor = new SecretRedactor();
        var request = await CaravanaDeployer.BuildRequestAsync(resource, builder.Resources, BananaCompose(), new() { ["API_IMAGE"] = "x" }, new ListLogger(), redactor, Ct);
        var handler = Caravana("0.22.0"); // has providers, not templates
        var flow = new CaravanaDeployFlow(new CaravanaApiClient(handler.Client(), Token, redactor), redactor, new ListLogger());

        var ex = await Assert.ThrowsAsync<CaravanaDeployException>(() => flow.RunAsync(PlanOf(request), Ct));

        Assert.Equal(["GET /v1/health"], handler.Requests.Select(r => r.ToString()).ToArray());
        Assert.Contains("secret templates", ex.Message);
        Assert.Contains(CaravanaVersions.MinForSecretTemplates.ToString(), ex.Message);
        Assert.Contains("0.22.0", ex.Message);
        Assert.Contains("Nothing was sent", ex.Message);
    }

    [Fact]
    public async Task ACaravanaThatIsNewEnough_GetsTheDeploy_AfterTheHealthCheck()
    {
        var (_, handler, _) = await DeployBanana(CaravanaVersions.MinForSecretTemplates.ToString());

        Assert.Equal(
            ["GET /v1/health", "GET /v1/apps/shop", "POST /v1/apps/shop/compose"],
            handler.Requests.Select(r => r.ToString()).ToArray()
        );
    }

    [Fact]
    public async Task ADevBuild_OrNoVersion_IsAllowed_WithAWarning()
    {
        var (_, devHandler, devLog) = await DeployBanana("dev");
        var (_, noneHandler, _) = await DeployBanana(null);

        Assert.Contains(devHandler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains(noneHandler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Contains("can't be compared", devLog.All);
    }

    [Fact]
    public async Task IfTheVersionCantBeRead_TheDeployIsRefused_NothingSent()
    {
        var thrown = await Assert.ThrowsAsync<CaravanaDeployException>(() => DeployBanana("0.99.0", healthFails: true));

        Assert.Contains("could not read it", thrown.Message);
        Assert.Contains("Nothing was sent", thrown.Message);
    }

    [Fact]
    public async Task APlainFromReference_NeedsTheProviderRelease_NotTheTemplateOne()
    {
        var redactor = new SecretRedactor();
        var request = new ComposeRequest(
            "services:\n  web:\n    image: x\n",
            null,
            new Dictionary<string, ComposeOverlay>
            {
                ["web"] = new() { Secrets = [OverlaySecret.FromProvider("X", new SecretReference("bella-prod", "X"))] },
            }
        );

        var old = Caravana("0.20.9");
        var oldEx = await Assert.ThrowsAsync<CaravanaDeployException>(() =>
            new CaravanaDeployFlow(new CaravanaApiClient(old.Client(), Token, redactor), redactor, new ListLogger()).RunAsync(PlanOf(request), Ct)
        );
        Assert.Contains("0.21.0", oldEx.Message);
        Assert.Single(old.Requests);

        var ok = Caravana("0.21.0");
        await new CaravanaDeployFlow(new CaravanaApiClient(ok.Client(), Token, redactor), redactor, new ListLogger()).RunAsync(PlanOf(request), Ct);
        Assert.Contains(ok.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ARequestWithOnlyTypedSecrets_NeverAsksForTheVersion()
    {
        var redactor = new SecretRedactor();
        var request = new ComposeRequest(
            "services:\n  web:\n    image: x\n",
            null,
            new Dictionary<string, ComposeOverlay> { ["web"] = new() { Secrets = [new OverlaySecret("X", "typed-value")] } }
        );
        var handler = Caravana("0.1.0");

        await new CaravanaDeployFlow(new CaravanaApiClient(handler.Client(), Token, redactor), redactor, new ListLogger()).RunAsync(PlanOf(request), Ct);

        Assert.DoesNotContain(handler.Requests, r => r.Path == "/v1/health");
    }

    [Theory]
    [InlineData("0.23.0", 0, 23, 0)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("0.23.1-rc.1", 0, 23, 1)]
    [InlineData(" 10.20.30+build5 ", 10, 20, 30)]
    public void Versions_AreReadFromTheirStart(string text, int major, int minor, int patch)
    {
        Assert.True(CaravanaVersions.TryParse(text, out var version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1.2")]
    [InlineData("abc")]
    public void ATextThatIsNotAVersion_IsAllowed(string? text)
    {
        Assert.False(CaravanaVersions.TryParse(text, out _));
        Assert.True(CaravanaVersions.IsAtLeast(text, new Version(99, 0, 0)));
    }

    [Fact]
    public void TheMinimumVersions_AreOrdered()
    {
        Assert.True(CaravanaVersions.MinForSecretTemplates > CaravanaVersions.MinForProviderReferences);
        Assert.False(CaravanaVersions.IsAtLeast("0.20.0", CaravanaVersions.MinForProviderReferences));
        Assert.True(CaravanaVersions.IsAtLeast("0.21.0", CaravanaVersions.MinForProviderReferences));
        Assert.False(CaravanaVersions.IsAtLeast("0.22.9", CaravanaVersions.MinForSecretTemplates));
    }
}
