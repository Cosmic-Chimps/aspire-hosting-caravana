using Aspire.Hosting;
using Aspire.Hosting.Pipelines;
using Xunit;

namespace CosmicChimps.Aspire.Hosting.Caravana.Tests;

/// <summary>
/// Guards what the <c>caravana-deploy-{name}</c> pipeline step waits for. A timing bug can't be
/// pinned by a timing test, so these assert the declaration: the dependency exists or it doesn't.
/// </summary>
public class DeployStepDependencyTests
{
    private static PipelineStep BuildDeployStep(string appName)
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);

        var caravana = builder.PublishToCaravana(appName, s =>
        {
            s.Url = "https://caravana.example.com";
            s.Token = "token-value";
        });

        var annotation = Assert.Single(caravana.Resource.Annotations.OfType<PipelineStepAnnotation>());

        // The factory ignores its context, so null is safe and keeps the test free of pipeline plumbing.
        var steps = annotation.CreateStepsAsync(null!).GetAwaiter().GetResult();

        return Assert.Single(steps, s => s.Name == $"caravana-deploy-{appName}");
    }

    [Fact]
    public void TheDeployStep_WaitsForTheComposeEnvironmentToBePrepared()
    {
        var step = BuildDeployStep("shop");

        Assert.Contains("prepare-shop-compose", step.DependsOnSteps);
    }

    [Fact]
    public void ThePrepareDependency_IsDerivedFromTheAppName()
    {
        var step = BuildDeployStep("other-app");

        Assert.Contains("prepare-other-app-compose", step.DependsOnSteps);
    }

    [Fact]
    public void TheDeployStep_WaitsForEveryImageToBePushed()
    {
        // "build" only means the images exist locally; every push-{resource} step is required by
        // the "push" meta-step, so waiting on build alone can name a tag the registry lacks.
        var step = BuildDeployStep("shop");

        Assert.Contains(WellKnownPipelineSteps.Push, step.DependsOnSteps);
    }

    [Fact]
    public void TheDeployStep_WaitsForPublishAndBuild_AndIsRequiredByDeploy()
    {
        var step = BuildDeployStep("shop");

        Assert.Contains("publish", step.DependsOnSteps);
        Assert.Contains("build", step.DependsOnSteps);
        Assert.Contains("deploy", step.RequiredBySteps);
    }

    [Fact]
    public void TheComposeUpStep_IsReconfigured()
    {
        // With Caravana as the target there is no local daemon to run docker compose up against.
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var caravana = builder.PublishToCaravana("shop");

        Assert.Single(caravana.Resource.Annotations.OfType<PipelineConfigurationAnnotation>());
    }

    [Fact]
    public void PublishToCaravana_AddsTheComposeEnvironmentNamedAfterTheApp()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var caravana = builder.PublishToCaravana("shop");

        Assert.Equal("shop-compose", caravana.Resource.ComposeEnvironment.Name);
    }

    [Fact]
    public async Task Settings_AreDeferred_AndResolvedWithDefaults()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        builder.Configuration["Parameters:caravana-url"] = "https://c.example.com";
        builder.Configuration["Parameters:caravana-token"] = "cvat_test-token";
        var url = builder.AddParameter("caravana-url");
        var token = builder.AddParameter("caravana-token", secret: true);

        var caravana = builder.PublishToCaravana("shop", s =>
        {
            s.Url = url.AsCaravanaValue();
            s.Token = token.AsCaravanaValue();
        });

        // Nothing resolved while the model is built.
        Assert.Equal(string.Empty, caravana.Resource.Url);

        await caravana.Resource.ResolveConfigurationAsync(CancellationToken.None);

        Assert.Equal("https://c.example.com", caravana.Resource.Url);
        Assert.Equal("cvat_test-token", caravana.Resource.Token);
        Assert.Equal("default", caravana.Resource.Project);
        Assert.Equal("production", caravana.Resource.Environment);
        Assert.Equal("shop", caravana.Resource.AppName);
    }

    [Fact]
    public async Task ARequiredSettingThatResolvesEmpty_NamesTheSetting()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var caravana = builder.PublishToCaravana("shop", s => s.Url = "https://c.example.com");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            caravana.Resource.ResolveConfigurationAsync(CancellationToken.None)
        );

        Assert.Contains("'Token'", ex.Message);
    }

    [Fact]
    public void ToString_NeverPrintsAValue()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        builder.Configuration["Parameters:caravana-token"] = "cvat_secret-token";
        var token = builder.AddParameter("caravana-token", secret: true);

        Assert.Equal("<parameter:caravana-token>", token.AsCaravanaValue().ToString());
        Assert.DoesNotContain("cvat_secret-token", CaravanaValue.FromLiteral("cvat_secret-token").ToString());
    }

    [Theory]
    [InlineData("default", "production", "shop", "shop")]
    [InlineData("shop", "production", "web", "shop--production--web")]
    [InlineData("default", "staging", "web", "default--staging--web")]
    public void TheAppId_FollowsCaravanasPlaceRule(string project, string environment, string name, string id)
    {
        Assert.Equal(id, CaravanaPlace.AppId(project, environment, name));
    }

    [Theory]
    [InlineData("Shop")]
    [InlineData("a--b")]
    [InlineData("-a")]
    [InlineData("a_b")]
    public void AnInvalidName_IsReported(string name)
    {
        Assert.NotEmpty(CaravanaPlace.Validate("default", "production", name));
    }
}
