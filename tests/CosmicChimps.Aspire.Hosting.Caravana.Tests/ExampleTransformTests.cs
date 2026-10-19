using Xunit;

namespace CosmicChimps.Aspire.Hosting.Caravana.Tests;

/// <summary>The example AppHost's real <c>aspire publish</c> output, through the transform.</summary>
public class ExampleTransformTests
{
    [Fact]
    public void TheExampleAppHost_BecomesOneAppWithTheOverlayItAsksFor()
    {
        var compose = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "example-docker-compose.yaml"));
        var env = new Dictionary<string, string>
        {
            ["WEB_IMAGE"] = "ghcr.io/acme/web:20261019",
            ["WEB_PORT"] = "8080",
            ["REDIS_PASSWORD"] = "s3cret-test",
        };

        var web = new CaravanaServiceSettings { HasHealth = true, HealthPath = "/health" };
        web.Domains.Add(("shop.example.com", null));
        var files = new CaravanaServiceSettings
        {
            HasHealth = true,
            HealthPort = 80,
            BackupEngine = CaravanaBackupEngine.Volume,
        };

        var result = CaravanaComposeTransformer.Transform(
            new TransformInput(
                compose,
                env,
                new Dictionary<string, string> { ["REDIS_PASSWORD"] = "s3cret-test" },
                DeployDashboard: false,
                new Dictionary<string, CaravanaServiceSettings> { ["web"] = web, ["files"] = files }
            )
        );

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.DoesNotContain("${", result.Compose);
        Assert.DoesNotContain("s3cret-test", result.Compose);
        Assert.DoesNotContain("aspire-dashboard", result.Compose);
        Assert.Contains("source: \"files-data\"", result.Compose);

        Assert.Equal(["REDIS_PASSWORD"], result.Services["cache"].Secrets!.Select(s => s.Name));
        Assert.Equal(
            ["CACHE_PASSWORD", "CACHE_URI", "ConnectionStrings__cache"],
            result.Services["web"].Secrets!.Select(s => s.Name).Order(StringComparer.Ordinal).ToArray()
        );
        Assert.Equal("shop.example.com", result.Services["web"].Domains![0].Host);
        Assert.Equal("/health", result.Services["web"].Health!.Path);
        Assert.Equal("volume", result.Services["files"].Backup!.Engine);
        Assert.Equal(80, result.Services["files"].Health!.Port);
    }
}
