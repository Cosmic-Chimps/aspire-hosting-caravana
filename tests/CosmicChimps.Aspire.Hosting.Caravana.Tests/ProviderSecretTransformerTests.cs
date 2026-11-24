using System.Text.Json;
using CosmicChimps.Aspire.Hosting.Caravana.Models;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace CosmicChimps.Aspire.Hosting.Caravana.Tests;

/// <summary>
/// Provider secrets in the transform: a parameter's stand-in becomes a reference or a template and never
/// reaches the file or the overlay.
/// </summary>
public class ProviderSecretTransformerTests
{
    private const string DbSentinel = "caravana-ref-00000000000000000000000000000001";
    private const string JwtSentinel = "caravana-ref-00000000000000000000000000000002";
    private const string RealValue = "real-db-password-from-config";

    private static ProviderSecret Db(string provider = "bella-prod", string path = "DB_PASSWORD") =>
        new("db-password", provider, path, DbSentinel);

    private static ProviderSecret Jwt() => new("jwt-key", "bella-prod", "JWT_KEY", JwtSentinel);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static Dictionary<string, string> BananaEnv() =>
        new()
        {
            ["API_IMAGE"] = "ghcr.io/acme/api:1",
            // What a stale or hand-made .env could hold: the real value. It must never be read.
            ["DB_PASSWORD"] = RealValue,
        };

    private static TransformResult Run(
        string compose,
        Dictionary<string, string>? env = null,
        Dictionary<string, string>? secrets = null,
        params ProviderSecret[] providers
    ) =>
        CaravanaComposeTransformer.Transform(
            new TransformInput(compose, env ?? BananaEnv(), secrets ?? [], false, new Dictionary<string, CaravanaServiceSettings>(), providers)
        );

    private static TransformResult Banana() => Run(Fixture("banana-docker-compose.yaml"), providers: Db());

    private static string Json(object value) => JsonSerializer.Serialize(value, CaravanaJson.Options);

    private static string? EnvValue(string compose, string service, string key)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(compose));
        var services = (YamlMappingNode)((YamlMappingNode)stream.Documents[0].RootNode).Children[new YamlScalarNode("services")];
        var svc = (YamlMappingNode)services.Children[new YamlScalarNode(service)];
        if (!svc.Children.TryGetValue(new YamlScalarNode("environment"), out var env))
            return null;
        return ((YamlMappingNode)env).Children.TryGetValue(new YamlScalarNode(key), out var v) ? ((YamlScalarNode)v).Value : null;
    }

    [Fact]
    public void AnEnvironmentValueThatIsExactlyTheStandIn_BecomesAFromReference()
    {
        var result = Banana();

        Assert.True(result.Success, string.Join("; ", result.Errors));
        var keycloak = Assert.Single(result.Services["keycloak"].Secrets!);
        Assert.Equal("KC_DB_PASSWORD", keycloak.Name);
        Assert.Equal(new SecretReference("bella-prod", "DB_PASSWORD"), keycloak.From);
        Assert.Null(keycloak.Value);
        Assert.Null(keycloak.Template);
        Assert.Null(keycloak.Refs);
        var postgres = Assert.Single(result.Services["postgres"].Secrets!);
        Assert.Equal("POSTGRES_PASSWORD", postgres.Name);
        // The entry leaves the file; the non-secret ones stay.
        Assert.Null(EnvValue(result.Compose!, "keycloak", "KC_DB_PASSWORD"));
        Assert.Equal("postgres", EnvValue(result.Compose!, "keycloak", "KC_DB_USERNAME"));
    }

    [Fact]
    public void AConnectionString_BecomesATemplateWithARef()
    {
        var api = Banana().Services["api"].Secrets!;

        var connection = Assert.Single(api);
        Assert.Equal("ConnectionStrings__postgres", connection.Name);
        Assert.Equal("Host=postgres;Port=5432;Username=postgres;Password=${db_password};Database=banana", connection.Template);
        Assert.Equal(new SecretReference("bella-prod", "DB_PASSWORD"), Assert.Single(connection.Refs!).Value);
        Assert.Equal("db_password", Assert.Single(connection.Refs!).Key);
        Assert.Null(connection.From);
        Assert.Null(connection.Value);
    }

    [Fact]
    public void BananasOverlay_IsExactlyTheThreeShapesOfTheWire()
    {
        var result = Banana();

        Assert.Equal(
            """[{"name":"KC_DB_PASSWORD","from":{"provider":"bella-prod","path":"DB_PASSWORD"}}]""",
            Json(result.Services["keycloak"].Secrets!)
        );
        Assert.Equal(
            """[{"name":"ConnectionStrings__postgres","template":"Host=postgres;Port=5432;Username=postgres;Password=${db_password};Database=banana","refs":{"db_password":{"provider":"bella-prod","path":"DB_PASSWORD"}}}]""",
            Json(result.Services["api"].Secrets!)
        );
        Assert.Equal(
            """[{"name":"x","value":"typed"}]""",
            Json(new List<OverlaySecret> { new("x", "typed") })
        );
    }

    [Fact]
    public void NoStandInAndNoRealValue_IsLeftInTheFileOrTheOverlay()
    {
        var result = Banana();

        var overlay = Json(result.Services);
        foreach (var text in new[] { result.Compose!, overlay })
        {
            Assert.DoesNotContain(DbSentinel, text);
            Assert.DoesNotContain("caravana-ref-", text);
            Assert.DoesNotContain(RealValue, text);
        }
        Assert.DoesNotContain("${", result.Compose);
    }

    [Fact]
    public void ALiteralDollarSign_InTheTextAroundAStandIn_IsWrittenDoubled()
    {
        // "a$$b" in the file is Compose's way to say "a$b"; a value from a variable that holds a "$" is escaped the same way.
        const string compose = """
            services:
              web:
                image: "x"
                environment:
                  CONN: "Host=db;Password=${DB_PASSWORD};Opts=a$$b;Extra=${OPTS}"
            """;
        var env = new Dictionary<string, string> { ["OPTS"] = "p$q" };

        var result = Run(compose, env, providers: Db());

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal("Host=db;Password=${db_password};Opts=a$$b;Extra=p$$q", result.Services["web"].Secrets![0].Template);
    }

    [Fact]
    public void ADollarSignAtTheEdges_Survives()
    {
        const string compose = """
            services:
              web:
                image: "x"
                environment:
                  CONN: "$$start;${DB_PASSWORD};end$$"
            """;

        var result = Run(compose, [], providers: Db());

        Assert.Equal("$$start;${db_password};end$$", result.Services["web"].Secrets![0].Template);
    }

    [Fact]
    public void TwoDifferentRefsInOneValue_AndTheSameOneTwice()
    {
        const string compose = """
            services:
              web:
                image: "x"
                environment:
                  BOTH: "db=${DB_PASSWORD};jwt=${JWT_KEY};again=${DB_PASSWORD}"
            """;

        var result = Run(compose, [], providers: [Db(), Jwt()]);

        var secret = Assert.Single(result.Services["web"].Secrets!);
        Assert.Equal("db=${db_password};jwt=${jwt_key};again=${db_password}", secret.Template);
        Assert.Equal(["db_password", "jwt_key"], secret.Refs!.Keys.Order().ToArray());
        Assert.Equal("JWT_KEY", secret.Refs["jwt_key"].Path);
        Assert.DoesNotContain("caravana-ref-", Json(result.Services));
    }

    [Fact]
    public void TwoStandInsWithNothingBetween_AreATemplateNotAFrom()
    {
        const string compose = """
            services:
              web:
                image: "x"
                environment:
                  BOTH: "${DB_PASSWORD}${JWT_KEY}"
            """;

        var result = Run(compose, [], providers: [Db(), Jwt()]);

        Assert.Equal("${db_password}${jwt_key}", result.Services["web"].Secrets![0].Template);
    }

    [Fact]
    public void TheSameParameterInTwoServices_IsAReferenceInEach_AndTwoParametersMayShareAPath()
    {
        var result = Banana();
        Assert.Equal("DB_PASSWORD", result.Services["keycloak"].Secrets![0].From!.Path);
        Assert.Equal("DB_PASSWORD", result.Services["postgres"].Secrets![0].From!.Path);
        Assert.Equal("DB_PASSWORD", result.Services["api"].Secrets![0].Refs!["db_password"].Path);
    }

    [Fact]
    public void RefNames_AreMadeToMatchCarvanasPattern_AndStayUnique()
    {
        Assert.Equal("db_password", CaravanaComposeTransformer.RefName("db-password", []));
        Assert.Equal("_1st", CaravanaComposeTransformer.RefName("1st", []));
        Assert.Equal("a_b", CaravanaComposeTransformer.RefName("a.b", []));
        Assert.Equal("a_b_2", CaravanaComposeTransformer.RefName("a-b", ["a_b"]));
        Assert.Equal("a_b_3", CaravanaComposeTransformer.RefName("a-b", ["a_b", "a_b_2"]));
        var long64 = CaravanaComposeTransformer.RefName(new string('x', 100), []);
        Assert.Equal(64, long64.Length);
        Assert.Equal(64, CaravanaComposeTransformer.RefName(new string('x', 100), [long64]).Length);
        Assert.Matches("^[A-Za-z_][A-Za-z0-9_]{0,63}$", CaravanaComposeTransformer.RefName("café-☃", []));
    }

    [Fact]
    public void AProviderSecretMixedWithATypedSecret_IsRefused_NamingTheServiceAndTheVariable()
    {
        const string compose = """
            services:
              web:
                image: "x"
                environment:
                  MIXED: "a=${DB_PASSWORD};b=${CACHE_PASSWORD}"
            """;
        var env = new Dictionary<string, string> { ["CACHE_PASSWORD"] = "typed-secret-value" };

        var result = Run(compose, env, new() { ["CACHE_PASSWORD"] = "typed-secret-value" }, Db());

        Assert.False(result.Success);
        Assert.Null(result.Compose);
        var error = Assert.Single(result.Errors);
        Assert.Contains("'MIXED'", error);
        Assert.Contains("'web'", error);
        Assert.Contains("mixes a provider secret", error);
        Assert.DoesNotContain("typed-secret-value", error);
        Assert.DoesNotContain(DbSentinel, error);
    }

    [Fact]
    public void AProviderSecretAloneAndATypedSecretAlone_BothWorkInOneService()
    {
        const string compose = """
            services:
              web:
                image: "x"
                environment:
                  REF: "${DB_PASSWORD}"
                  TYPED: "${CACHE_PASSWORD}"
            """;
        var env = new Dictionary<string, string> { ["CACHE_PASSWORD"] = "typed-secret-value" };

        var result = Run(compose, env, new() { ["CACHE_PASSWORD"] = "typed-secret-value" }, Db());

        Assert.True(result.Success, string.Join("; ", result.Errors));
        var secrets = result.Services["web"].Secrets!.ToDictionary(s => s.Name);
        Assert.Equal("DB_PASSWORD", secrets["REF"].From!.Path);
        Assert.Equal("typed-secret-value", secrets["TYPED"].Value);
    }

    [Fact]
    public void AStandInInACommand_IsRefused_NamingTheField()
    {
        const string compose = """
            services:
              web:
                image: "x"
                command:
                  - "serve"
                  - "--password=${DB_PASSWORD}"
            """;

        var result = Run(compose, [], providers: Db());

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Contains("services.web.command[1]", error);
        Assert.Contains("'db-password'", error);
        Assert.DoesNotContain(DbSentinel, error);
    }

    [Fact]
    public void AStandInInALabel_IsRefused()
    {
        const string compose = """
            services:
              web:
                image: "x"
                labels:
                  secret: "${DB_PASSWORD}"
            """;

        var result = Run(compose, [], providers: Db());

        Assert.False(result.Success);
        Assert.Contains("services.web.labels.secret", Assert.Single(result.Errors));
    }

    [Fact]
    public void ARefInAnInvalidSecretName_IsRefused()
    {
        const string compose = """
            services:
              web:
                image: "x"
                environment:
                  "my-var": "${DB_PASSWORD}"
            """;

        var result = Run(compose, [], providers: Db());

        Assert.False(result.Success);
        Assert.Contains("'my-var'", Assert.Single(result.Errors));
    }

    [Fact]
    public void TheListFormOfEnvironment_Works()
    {
        const string compose = """
            services:
              web:
                image: "x"
                environment:
                  - "REF=${DB_PASSWORD}"
                  - "CONN=Password=${DB_PASSWORD};X=1"
                  - "PLAIN=1"
            """;

        var result = Run(compose, [], providers: Db());

        Assert.True(result.Success, string.Join("; ", result.Errors));
        var secrets = result.Services["web"].Secrets!.ToDictionary(s => s.Name);
        Assert.Equal("DB_PASSWORD", secrets["REF"].From!.Path);
        Assert.Equal("Password=${db_password};X=1", secrets["CONN"].Template);
        Assert.Contains("PLAIN=1", result.Compose);
        Assert.DoesNotContain("REF=", result.Compose);
    }

    [Fact]
    public void MoreThan16RefsInOneValue_IsRefused()
    {
        var providers = Enumerable
            .Range(1, 17)
            .Select(i => new ProviderSecret($"p{i}", "bella-prod", $"P{i}", $"caravana-ref-{i:x32}"))
            .ToArray();
        var value = string.Join(";", Enumerable.Range(1, 17).Select(i => $"${{P{i}}}"));
        var compose = $"services:\n  web:\n    image: \"x\"\n    environment:\n      BIG: \"{value}\"\n";

        var result = Run(compose, [], providers: providers);

        Assert.False(result.Success);
        Assert.Contains("17 provider secrets", Assert.Single(result.Errors));
    }

    [Fact]
    public void ATemplateOver4096Bytes_IsRefused()
    {
        var compose = $"services:\n  web:\n    image: \"x\"\n    environment:\n      BIG: \"{new string('a', 4100)}${{DB_PASSWORD}}\"\n";

        var result = Run(compose, [], providers: Db());

        Assert.False(result.Success);
        Assert.Contains("4096", Assert.Single(result.Errors));
    }

    [Fact]
    public void ARealValueForTheParameter_InTheSecretsOrTheEnvFile_IsNeverUsed()
    {
        var result = Run(
            Fixture("banana-docker-compose.yaml"),
            BananaEnv(),
            new() { ["DB_PASSWORD"] = RealValue },
            Db()
        );

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.DoesNotContain(RealValue, result.Compose);
        Assert.DoesNotContain(RealValue, Json(result.Services));
        Assert.All(result.Services.Values.SelectMany(s => s.Secrets!), s => Assert.Null(s.Value));
    }

    [Fact]
    public void ToString_NeverPrintsATypedValue_AndMayShowAReferenceOrATemplate()
    {
        var result = Banana();

        Assert.Equal("OverlaySecret { Name = x }", new OverlaySecret("x", "hunter2-secret").ToString());
        Assert.Equal(
            "OverlaySecret { Name = KC_DB_PASSWORD, From = bella-prod:DB_PASSWORD }",
            result.Services["keycloak"].Secrets![0].ToString()
        );
        var template = result.Services["api"].Secrets![0].ToString();
        Assert.Contains("Template = Host=postgres", template);
        Assert.Contains("db_password=bella-prod:DB_PASSWORD", template);
    }

    [Fact]
    public void TheExampleAppHostsRealOutput_WithItsRedisPasswordAsAProviderSecret()
    {
        // Aspire's own output for the example AppHost, published with the parameter marked: the .env file
        // holds the stand-in where the typed value would be (ran with Aspire 13.6.4, 2026-11-24).
        var env = new Dictionary<string, string>
        {
            ["WEB_IMAGE"] = "ghcr.io/acme/web:20261019",
            ["WEB_PORT"] = "8080",
            ["REDIS_PASSWORD"] = "caravana-ref-cd469ad2849d4ccf919d33abc335e7c0",
        };
        var provider = new ProviderSecret("redis-password", "bella-prod", "REDIS_PASSWORD", env["REDIS_PASSWORD"]);

        var result = Run(Fixture("example-docker-compose.yaml"), env, providers: provider);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.DoesNotContain("caravana-ref-", result.Compose);
        Assert.DoesNotContain("caravana-ref-", Json(result.Services));
        Assert.Equal("REDIS_PASSWORD", result.Services["cache"].Secrets![0].From!.Path);
        var web = result.Services["web"].Secrets!.ToDictionary(s => s.Name);
        Assert.Equal("cache:6379,password=${redis_password}", web["ConnectionStrings__cache"].Template);
        Assert.Equal("redis://:${redis_password}@cache:6379", web["CACHE_URI"].Template);
        Assert.Equal("REDIS_PASSWORD", web["CACHE_PASSWORD"].From!.Path);
        // The command's "$$REDIS_PASSWORD" is the container's own variable, not a stand-in.
        Assert.Contains("$$REDIS_PASSWORD", result.Compose);
    }

    [Fact]
    public void ADashboardlessFileWithoutProviderSecrets_BehavesAsBefore()
    {
        var result = CaravanaComposeTransformer.Transform(
            new TransformInput(
                Fixture("banana-docker-compose.yaml"),
                BananaEnv(),
                new Dictionary<string, string>(),
                false,
                new Dictionary<string, CaravanaServiceSettings>()
            )
        );

        // Without the marker, DB_PASSWORD is an ordinary variable: its value (non-secret here) is substituted.
        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Contains(RealValue, result.Compose);
    }
}
