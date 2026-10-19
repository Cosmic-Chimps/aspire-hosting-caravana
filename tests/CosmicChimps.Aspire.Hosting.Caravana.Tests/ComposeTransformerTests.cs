using System.Text.Json;
using CosmicChimps.Aspire.Hosting.Caravana.Models;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace CosmicChimps.Aspire.Hosting.Caravana.Tests;

/// <summary>
/// The transform on real Aspire 13.6.1 output (the fixture), with a fake .env file.
/// </summary>
public class ComposeTransformerTests
{
    private const string CachePassword = "s3cret-test";

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static Dictionary<string, string> Env() => EnvFile.Parse(Fixture("env.Production.fixture"));

    private static Dictionary<string, string> Secrets() => new() { ["CACHE_PASSWORD"] = CachePassword };

    private static TransformResult Run(
        bool dashboard = false,
        Dictionary<string, CaravanaServiceSettings>? settings = null,
        Dictionary<string, string>? env = null,
        Dictionary<string, string>? secrets = null,
        string? compose = null
    ) =>
        CaravanaComposeTransformer.Transform(
            new TransformInput(
                compose ?? Fixture("docker-compose.yaml"),
                env ?? Env(),
                secrets ?? Secrets(),
                dashboard,
                settings ?? []
            )
        );

    private static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlMappingNode Service(string compose, string name) =>
        (YamlMappingNode)((YamlMappingNode)Parse(compose).Children[new YamlScalarNode("services")]).Children[
            new YamlScalarNode(name)
        ];

    private static string? EnvValue(string compose, string service, string key)
    {
        var svc = Service(compose, service);
        if (!svc.Children.TryGetValue(new YamlScalarNode("environment"), out var env))
            return null;
        return ((YamlMappingNode)env).Children.TryGetValue(new YamlScalarNode(key), out var v)
            ? ((YamlScalarNode)v).Value
            : null;
    }

    [Fact]
    public void NoVariableIsLeft_ExceptTheDoubleDollarEscape()
    {
        var result = Run();

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.DoesNotContain("${", result.Compose);
        // Compose's own escape for a literal "$" survives: Caravana reads it as "$".
        Assert.Contains("$$REDIS_PASSWORD", result.Compose);
    }

    [Fact]
    public void ImagesAndPorts_AreSubstituted()
    {
        var compose = Run().Compose!;

        Assert.Equal("ghcr.io/acme/apiservice:20261019", ((YamlScalarNode)Service(compose, "apiservice").Children[new YamlScalarNode("image")]).Value);
        Assert.Equal("ghcr.io/acme/webfrontend:20261019", ((YamlScalarNode)Service(compose, "webfrontend").Children[new YamlScalarNode("image")]).Value);
        Assert.Equal("8080", EnvValue(compose, "apiservice", "HTTP_PORTS"));
        Assert.Equal("http://apiservice:8080", EnvValue(compose, "webfrontend", "services__apiservice__http__0"));
    }

    [Fact]
    public void TheDashboard_IsDroppedByDefault_WithItsReferences()
    {
        var compose = Run().Compose!;

        Assert.DoesNotContain("aspire-dashboard", compose);
        Assert.DoesNotContain("demo-aspire-compose-dashboard", compose);
        Assert.Null(EnvValue(compose, "apiservice", "OTEL_EXPORTER_OTLP_ENDPOINT"));
        Assert.Null(EnvValue(compose, "webfrontend", "OTEL_EXPORTER_OTLP_ENDPOINT"));
        // The other OTEL settings are about the service itself and stay.
        Assert.Equal("apiservice", EnvValue(compose, "apiservice", "OTEL_SERVICE_NAME"));
    }

    [Fact]
    public void TheDashboard_IsKept_WhenAskedFor_WithAFrontPortForItsHealthCheck()
    {
        var result = Run(dashboard: true);

        Assert.Contains("demo-aspire-compose-dashboard", result.Compose);
        Assert.Equal("http://demo-aspire-compose-dashboard:18889", EnvValue(result.Compose!, "apiservice", "OTEL_EXPORTER_OTLP_ENDPOINT"));
        // 18888, 18889 and 18890: Caravana can't guess which is the front, so the publisher names it.
        var health = result.Services["demo-aspire-compose-dashboard"].Health;
        Assert.Equal(18888, health?.Port);
    }

    [Fact]
    public void TheVolume_GetsAStableValidName()
    {
        var compose = Run().Compose!;

        Assert.DoesNotContain("cosmicchimps.aspire.apphost", compose);
        var root = Parse(compose);
        var volumes = (YamlMappingNode)root.Children[new YamlScalarNode("volumes")];
        Assert.Equal(["seq-data"], volumes.Children.Keys.Select(k => ((YamlScalarNode)k).Value));

        var source = ((YamlScalarNode)((YamlMappingNode)((YamlSequenceNode)Service(compose, "seq").Children[new YamlScalarNode("volumes")])[0]).Children[new YamlScalarNode("source")]).Value;
        Assert.Equal("seq-data", source);

        // The same input gives the same name on every run.
        Assert.Equal(compose, Run().Compose);
    }

    [Theory]
    [InlineData("cosmicchimps.aspire.apphost-a6dba33c27-seq-data", "seq-data")]
    [InlineData("MyApp.AppHost-0123456789-Pg_Data", "pg_data")]
    [InlineData("plain-name", "plain-name")]
    [InlineData("Weird Name!", "weird-name-")]
    public void StableVolumeName_Examples(string original, string expected)
    {
        Assert.Equal(expected, CaravanaComposeTransformer.StableVolumeName(original));
    }

    [Fact]
    public void VolumeNames_ThatCollide_StayUnique()
    {
        const string compose = """
            services:
              a:
                image: "x"
                volumes:
                  - type: "volume"
                    source: "app-0123456789-data"
                    target: "/a"
                  - "other-bbbbbbbbbb-data:/b"
              b:
                image: "y"
                volumes:
                  - "app-aaaaaaaaaa-data:/c:ro"
            volumes:
              app-0123456789-data: {}
              other-bbbbbbbbbb-data: {}
              app-aaaaaaaaaa-data: {}
            """;

        var result = Run(compose: compose, env: [], secrets: []);

        var volumes = (YamlMappingNode)Parse(result.Compose!).Children[new YamlScalarNode("volumes")];
        var names = volumes.Children.Keys.Select(k => ((YamlScalarNode)k).Value!).ToList();
        Assert.Equal(3, names.Distinct().Count());
        Assert.Contains("data", names);
        Assert.Contains("data-2", names);
        Assert.Contains("data-3", names);
        Assert.Contains("data-3:/c:ro", result.Compose);
    }

    [Fact]
    public void Secrets_MoveToTheOverlay_WithResolvedValues_AndLeaveTheFile()
    {
        var result = Run();

        Assert.DoesNotContain(CachePassword, result.Compose);

        var cache = result.Services["cache"].Secrets!;
        Assert.Equal(["REDIS_PASSWORD"], cache.Select(s => s.Name));
        Assert.Equal(CachePassword, cache[0].Value);

        var web = result.Services["webfrontend"].Secrets!.ToDictionary(s => s.Name, s => s.Value);
        Assert.Equal(
            ["CACHE_PASSWORD", "CACHE_URI", "ConnectionStrings__cache"],
            web.Keys.Order(StringComparer.Ordinal).ToArray()
        );
        Assert.Equal(CachePassword, web["CACHE_PASSWORD"]);
        Assert.Equal($"redis://:{CachePassword}@cache:6379", web["CACHE_URI"]);
        Assert.Equal($"cache:6379,password={CachePassword}", web["ConnectionStrings__cache"]);

        // The non-secret entries of the same service stay in the file.
        Assert.Equal("cache", EnvValue(result.Compose!, "webfrontend", "CACHE_HOST"));
        Assert.Null(EnvValue(result.Compose!, "webfrontend", "CACHE_URI"));
    }

    [Fact]
    public void ASecretWithADollarSign_ReachesTheOverlayUnchanged_AndOtherValuesAreEscaped()
    {
        const string compose = """
            services:
              web:
                image: "x"
                environment:
                  DB: "host=db;password=${DB_PASSWORD}"
                  SALT: "${SALT}"
            """;
        var env = new Dictionary<string, string> { ["DB_PASSWORD"] = "pa$$word", ["SALT"] = "a$b" };

        var result = Run(compose: compose, env: env, secrets: new() { ["DB_PASSWORD"] = "pa$$word" });

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal("host=db;password=pa$$word", result.Services["web"].Secrets![0].Value);
        // A value that came from a variable is escaped, so Caravana reads it back as typed.
        Assert.Equal("a$$b", EnvValue(result.Compose!, "web", "SALT"));
    }

    [Fact]
    public void ASecretInACommand_IsRefused_NamingTheFieldButNotTheValue()
    {
        const string compose = """
            services:
              web:
                image: "x"
                command:
                  - "serve"
                  - "--token=${TOKEN}"
            """;

        var result = Run(compose: compose, env: new() { ["TOKEN"] = "tok-12345" }, secrets: new() { ["TOKEN"] = "tok-12345" });

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Contains("services.web.command[1]", error);
        Assert.Contains("'TOKEN'", error);
        Assert.DoesNotContain("tok-12345", error);
    }

    [Fact]
    public void AMissingVariable_IsAnErrorNamingIt()
    {
        var env = Env();
        env.Remove("APISERVICE_IMAGE");

        var result = Run(env: env);

        Assert.False(result.Success);
        Assert.Null(result.Compose);
        Assert.Contains(result.Errors, e => e.Contains("${APISERVICE_IMAGE}"));
    }

    [Fact]
    public void AnEmptyVariable_IsAMissingVariable_UnlessItHasADefault()
    {
        const string compose = """
            services:
              web:
                image: "${IMAGE}"
                environment:
                  MODE: "${MODE:-production}"
            """;

        var result = Run(compose: compose, env: new() { ["IMAGE"] = "" }, secrets: []);

        Assert.Contains(result.Errors, e => e.Contains("${IMAGE}"));
        Assert.DoesNotContain(result.Errors, e => e.Contains("MODE"));
    }

    [Fact]
    public void ASecretWithoutAValueInTheEnvFile_UsesTheSecretsValue()
    {
        var env = Env();
        env["CACHE_PASSWORD"] = "";

        var result = Run(env: env);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(CachePassword, result.Services["cache"].Secrets![0].Value);
    }

    [Fact]
    public void Memory_IsWrittenAsReservationAndLimit()
    {
        var settings = new Dictionary<string, CaravanaServiceSettings>
        {
            ["apiservice"] = new() { ReservedMB = 256, MaxMB = 512 },
            ["seq"] = new() { ReservedMB = 128 },
        };

        var compose = Run(settings: settings).Compose!;

        Assert.Equal("256m", ((YamlScalarNode)Service(compose, "apiservice").Children[new YamlScalarNode("mem_reservation")]).Value);
        Assert.Equal("512m", ((YamlScalarNode)Service(compose, "apiservice").Children[new YamlScalarNode("mem_limit")]).Value);
        Assert.Equal("128m", ((YamlScalarNode)Service(compose, "seq").Children[new YamlScalarNode("mem_reservation")]).Value);
        Assert.False(Service(compose, "seq").Children.ContainsKey(new YamlScalarNode("mem_limit")));
    }

    [Fact]
    public void DomainHealthAndBackup_GoToTheOverlay()
    {
        var web = new CaravanaServiceSettings { HasHealth = true, HealthPath = "/health" };
        web.Domains.Add(("shop.example.com", null));
        var cache = new CaravanaServiceSettings
        {
            BackupEngine = CaravanaBackupEngine.Volume,
            BackupSchedule = "0 4 * * *",
            BackupKeep = 5,
        };

        var result = Run(settings: new() { ["webfrontend"] = web, ["seq"] = cache });

        var overlay = result.Services["webfrontend"];
        Assert.Equal([new OverlayDomain("shop.example.com", null)], overlay.Domains);
        Assert.Equal(new OverlayHealth("/health", null), overlay.Health);
        Assert.Equal(new OverlayBackup("volume", "0 4 * * *", 5), result.Services["seq"].Backup);
    }

    [Fact]
    public void ExcludedResources_AreDropped_WithTheirDependsOn()
    {
        var result = Run(settings: new() { ["cache"] = new() { Exclude = true } });

        Assert.True(result.Success, string.Join("; ", result.Errors));
        var depends = (YamlMappingNode)Service(result.Compose!, "webfrontend").Children[new YamlScalarNode("depends_on")];
        Assert.Equal(["apiservice"], depends.Children.Keys.Select(k => ((YamlScalarNode)k).Value));
        Assert.DoesNotContain("redis:8.6", result.Compose);
        // The web service still refers to the excluded one: said, not hidden.
        Assert.Contains(result.Warnings, w => w.Contains("'webfrontend'") && w.Contains("'cache'"));
    }

    [Fact]
    public void ASettingForAResourceTheFileLacks_IsAnError()
    {
        var result = Run(settings: new() { ["ghost"] = new() { ReservedMB = 64 } });

        Assert.Contains(result.Errors, e => e.Contains("'ghost'"));
    }

    [Fact]
    public void AVolumeOnlyAnExcludedServiceUsed_IsDropped()
    {
        var result = Run(settings: new() { ["seq"] = new() { Exclude = true } });

        Assert.DoesNotContain("seq-data", result.Compose);
        Assert.False(Parse(result.Compose!).Children.ContainsKey(new YamlScalarNode("volumes")));
    }

    [Fact]
    public void TheOverlay_SerializesToCaravanasJson_AndNeverPrintsASecretInToString()
    {
        var result = Run();
        var request = new ComposeRequest(result.Compose!, null, result.Services);

        var json = JsonSerializer.Serialize(request, CaravanaJson.Options);
        using var doc = JsonDocument.Parse(json);
        var secret = doc.RootElement.GetProperty("services").GetProperty("cache").GetProperty("secrets")[0];
        Assert.Equal("REDIS_PASSWORD", secret.GetProperty("name").GetString());
        Assert.Equal(CachePassword, secret.GetProperty("value").GetString());
        Assert.False(doc.RootElement.TryGetProperty("name", out _)); // null name omitted

        Assert.DoesNotContain(CachePassword, result.Services["cache"].Secrets![0].ToString());
        Assert.DoesNotContain(CachePassword, result.Services["cache"].ToString() ?? "");
    }

    [Fact]
    public void Services_WithNothingToSay_HaveNoOverlayEntry()
    {
        var result = Run();

        Assert.False(result.Services.ContainsKey("apiservice"));
    }

    [Fact]
    public void ListFormEnvironment_IsHandledToo()
    {
        const string compose = """
            services:
              web:
                image: "x"
                environment:
                  - "A=1"
                  - "B=pw-${PW}"
            """;

        var result = Run(compose: compose, env: new() { ["PW"] = "hunter22" }, secrets: new() { ["PW"] = "hunter22" });

        Assert.DoesNotContain("hunter22", result.Compose);
        Assert.Contains("A=1", result.Compose);
        Assert.Equal("pw-hunter22", result.Services["web"].Secrets![0].Value);
        Assert.Equal("B", result.Services["web"].Secrets![0].Name);
    }

    [Fact]
    public void InvalidYaml_IsAnError()
    {
        var result = Run(compose: "services: [unclosed");

        Assert.False(result.Success);
    }

    [Fact]
    public void EnvFile_ParsesCommentsQuotesAndExport()
    {
        var env = EnvFile.Parse("# c\n\nA=1\nexport B=\"two words\"\nC='x'\nD=\nnot a line\n");

        Assert.Equal("1", env["A"]);
        Assert.Equal("two words", env["B"]);
        Assert.Equal("x", env["C"]);
        Assert.Equal("", env["D"]);
        Assert.Equal(4, env.Count);
    }
}
