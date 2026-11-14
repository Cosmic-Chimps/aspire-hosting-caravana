using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using CosmicChimps.Aspire.Hosting.Caravana.Models;
using Xunit;

namespace CosmicChimps.Aspire.Hosting.Caravana.Tests;

/// <summary>The per-resource extensions and the secret parameters, as the deploy step reads them.</summary>
public class ResourceSettingsTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheExtensions_BecomeServiceSettings_WithParameterDomainsResolved()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        builder.Configuration["Parameters:shop-domain"] = "Shop.Example.com";
        var domain = builder.AddParameter("shop-domain");
        var caravana = builder.PublishToCaravana("shop");
        var web = builder.AddContainer("Web", "nginx");
        var db = builder.AddContainer("db", "postgres");

        web.WithCaravanaDomain(caravana, domain, port: 8080)
            .WithCaravanaDomain(caravana, "www.example.com")
            .WithCaravanaHealthCheck(caravana, "/health", 8080)
            .WithCaravanaMemory(caravana, 256, 512);
        db.WithCaravanaBackup(caravana, CaravanaBackupEngine.Postgres, "0 2 * * *", 3)
            .WithCaravanaScheduledRestore(caravana, "golden", " 0 4 * * * ")
            .WithCaravanaExclude(caravana);

        var settings = await CaravanaDeployer.CollectSettingsAsync(caravana.Resource, Ct);

        // The Compose service name is the lower-case resource name.
        var w = settings["web"];
        Assert.Equal([("shop.example.com", (int?)8080), ("www.example.com", null)], w.Domains);
        Assert.True(w.HasHealth);
        Assert.Equal("/health", w.HealthPath);
        Assert.Equal(8080, w.HealthPort);
        Assert.Equal(256, w.ReservedMB);
        Assert.Equal(512, w.MaxMB);

        var d = settings["db"];
        Assert.Equal(CaravanaBackupEngine.Postgres, d.BackupEngine);
        Assert.Equal("0 2 * * *", d.BackupSchedule);
        Assert.Equal(3, d.BackupKeep);
        Assert.Equal("golden", d.RestoreFrom);
        Assert.Equal("0 4 * * *", d.RestoreSchedule);
        Assert.True(d.Exclude);
        Assert.Null(w.RestoreFrom);
    }

    [Fact]
    public async Task AScheduledRestoreWithoutABackup_IsRefused()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var caravana = builder.PublishToCaravana("shop");
        builder.AddContainer("web", "nginx").WithCaravanaScheduledRestore(caravana, "golden", "0 4 * * *");

        var ex = await Assert.ThrowsAsync<CaravanaDeployException>(() => CaravanaDeployer.CollectSettingsAsync(caravana.Resource, Ct));

        Assert.Contains("'web'", ex.Message);
        Assert.Contains("WithCaravanaBackup", ex.Message);
    }

    [Fact]
    public async Task Identities_AreCollectedInOrder_WithTheBellaShorthand()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var caravana = builder.PublishToCaravana("shop");
        builder
            .AddContainer("web", "nginx")
            .WithCaravanaBellaIdentity(caravana, "env-123")
            .WithCaravanaIdentity(caravana, "other", ["a", "b"], TimeSpan.FromMinutes(10));

        var settings = await CaravanaDeployer.CollectSettingsAsync(caravana.Resource, Ct);

        Assert.Equal(
            [
                new OverlayIdentity("bella", ["bella-spiffe/env-123"]),
                new OverlayIdentity("other", ["a", "b"], 600),
            ],
            settings["web"].Identities,
            new IdentityComparer()
        );
    }

    [Fact]
    public async Task ADuplicateIdentityName_IsRefusedAtPublish()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var caravana = builder.PublishToCaravana("shop");
        builder.AddContainer("web", "nginx").WithCaravanaIdentity(caravana, "bella", ["a"]).WithCaravanaBellaIdentity(caravana, "env-1");

        var ex = await Assert.ThrowsAsync<CaravanaDeployException>(() => CaravanaDeployer.CollectSettingsAsync(caravana.Resource, Ct));

        Assert.Contains("'web'", ex.Message);
        Assert.Contains("'bella'", ex.Message);
    }

    [Fact]
    public async Task AFifthIdentity_IsRefusedAtPublish()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var caravana = builder.PublishToCaravana("shop");
        var web = builder.AddContainer("web", "nginx");
        for (var i = 1; i <= 5; i++)
            web.WithCaravanaIdentity(caravana, $"id{i}", ["a"]);

        var ex = await Assert.ThrowsAsync<CaravanaDeployException>(() => CaravanaDeployer.CollectSettingsAsync(caravana.Resource, Ct));

        Assert.Contains("'web'", ex.Message);
        Assert.Contains("4", ex.Message);
    }

    [Fact]
    public void BadIdentities_AreRefusedWhenTheModelIsBuilt()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var caravana = builder.PublishToCaravana("shop");
        var web = builder.AddContainer("web", "nginx");

        foreach (var name in new[] { "", "Bella", "1bella", "bel-la", "default", "vault_x", "consul_x", new string('a', 31) })
            Assert.Throws<ArgumentException>(() => web.WithCaravanaIdentity(caravana, name, ["a"]));
        Assert.Throws<ArgumentException>(() => web.WithCaravanaIdentity(caravana, "bella", []));
        Assert.Throws<ArgumentException>(() => web.WithCaravanaIdentity(caravana, "bella", ["1", "2", "3", "4", "5"]));
        Assert.Throws<ArgumentException>(() => web.WithCaravanaIdentity(caravana, "bella", ["has space"]));
        Assert.Throws<ArgumentException>(() => web.WithCaravanaIdentity(caravana, "bella", [""]));
        Assert.Throws<ArgumentException>(() => web.WithCaravanaIdentity(caravana, "bella", ["caf\u00e9"]));
        Assert.Throws<ArgumentException>(() => web.WithCaravanaIdentity(caravana, "bella", [new string('a', 256)]));
        Assert.Throws<ArgumentException>(() => web.WithCaravanaIdentity(caravana, "bella", ["a"], TimeSpan.FromMilliseconds(60500)));
        Assert.Throws<ArgumentOutOfRangeException>(() => web.WithCaravanaIdentity(caravana, "bella", ["a"], TimeSpan.FromSeconds(59)));
        Assert.Throws<ArgumentOutOfRangeException>(() => web.WithCaravanaIdentity(caravana, "bella", ["a"], TimeSpan.FromSeconds(86401)));
        Assert.Throws<ArgumentException>(() => web.WithCaravanaBellaIdentity(caravana, "has space"));
        Assert.Throws<ArgumentException>(() => web.WithCaravanaBellaIdentity(caravana, new string('a', 243)));
        web.WithCaravanaIdentity(caravana, "ok", [new string('a', 255)], TimeSpan.FromSeconds(86400));
        web.WithCaravanaBellaIdentity(caravana, new string('a', 242));
    }

    private sealed class IdentityComparer : IEqualityComparer<OverlayIdentity>
    {
        public bool Equals(OverlayIdentity? x, OverlayIdentity? y) =>
            x is not null && y is not null && x.Name == y.Name && x.TtlSeconds == y.TtlSeconds && x.Audience.SequenceEqual(y.Audience);

        public int GetHashCode(OverlayIdentity obj) => obj.Name.GetHashCode();
    }

    [Fact]
    public async Task ADomainThatIsAnAddress_IsRefused()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var caravana = builder.PublishToCaravana("shop");
        builder.AddContainer("web", "nginx").WithCaravanaDomain(caravana, "https://shop.example.com");

        var ex = await Assert.ThrowsAsync<CaravanaDeployException>(() => CaravanaDeployer.CollectSettingsAsync(caravana.Resource, Ct));

        Assert.Contains("host name", ex.Message);
    }

    [Fact]
    public void BadArguments_AreRefusedWhenTheModelIsBuilt()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var caravana = builder.PublishToCaravana("shop");
        var web = builder.AddContainer("web", "nginx");

        Assert.Throws<ArgumentOutOfRangeException>(() => web.WithCaravanaMemory(caravana, 512, 256));
        Assert.Throws<ArgumentOutOfRangeException>(() => web.WithCaravanaBackup(caravana, CaravanaBackupEngine.Volume, keep: 0));
        Assert.Throws<ArgumentException>(() => web.WithCaravanaHealthCheck(caravana, "health"));
        Assert.Throws<ArgumentException>(() => web.WithCaravanaScheduledRestore(caravana, "Golden", "0 4 * * *"));
        Assert.Throws<ArgumentException>(() => web.WithCaravanaScheduledRestore(caravana, "golden-", "0 4 * * *"));
        Assert.Throws<ArgumentException>(() => web.WithCaravanaScheduledRestore(caravana, "golden", "daily"));
        Assert.Throws<ArgumentException>(() => web.WithCaravanaScheduledRestore(caravana, "golden", "0 0 4 * * *"));
        Assert.Throws<ArgumentOutOfRangeException>(() => web.WithCaravanaDomain(caravana, "a.example.com", port: 70000));
    }

    [Fact]
    public async Task SecretParameters_AreFoundByTheirEnvName_WithTheEnvFilesValueFirst()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        builder.Configuration["Parameters:cache-password"] = "from-config";
        builder.Configuration["Parameters:api-key"] = "from-config-2";
        builder.Configuration["Parameters:plain-setting"] = "not-secret";
        builder.AddParameter("cache-password", secret: true);
        builder.AddParameter("api-key", secret: true);
        builder.AddParameter("plain-setting");

        var env = new Dictionary<string, string> { ["CACHE_PASSWORD"] = "from-env-file" };
        var secrets = await CaravanaDeployer.ResolveSecretsAsync(builder.Resources, env, Ct);

        Assert.Equal("from-env-file", secrets["CACHE_PASSWORD"]);
        Assert.Equal("from-config-2", secrets["API_KEY"]);
        Assert.DoesNotContain("PLAIN_SETTING", secrets.Keys);
    }

    [Fact]
    public async Task ASecretParameterWithNoValue_IsSkipped_NotFatal()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        builder.AddParameter("unset-secret", secret: true);

        var secrets = await CaravanaDeployer.ResolveSecretsAsync(builder.Resources, new Dictionary<string, string>(), Ct);

        Assert.Empty(secrets);
    }
}
