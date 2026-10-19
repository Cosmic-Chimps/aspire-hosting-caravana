using System.Net;
using System.Text.Json;
using CosmicChimps.Aspire.Hosting.Caravana.Models;
using Xunit;

namespace CosmicChimps.Aspire.Hosting.Caravana.Tests;

/// <summary>The API flow with a fake handler: create or update, retries, projects, outcomes, hygiene.</summary>
public class DeployFlowTests
{
    private const string Token = "cvat_token-test-0001";
    private const string Secret = "s3cret-test";
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static DeployPlan Plan(
        string project = "default",
        string environment = "production",
        bool wait = true,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? origin = null
    ) =>
        new(
            project,
            environment,
            "shop",
            new ComposeRequest(
                "services:\n  web:\n    image: \"x\"\n",
                null,
                new Dictionary<string, ComposeOverlay>
                {
                    ["web"] = new() { Secrets = [new OverlaySecret("DB_PASSWORD", Secret)] },
                }
            ),
            wait,
            timeout ?? TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(1),
            origin ?? new Dictionary<string, string>()
        );

    private static (CaravanaDeployFlow Flow, ListLogger Log, SecretRedactor Redactor) NewFlow(FakeHandler handler)
    {
        var redactor = new SecretRedactor();
        redactor.Add(Token);
        redactor.Add(Secret);
        var log = new ListLogger();
        return (new CaravanaDeployFlow(new CaravanaApiClient(handler.Client(), Token, redactor), redactor, log), log, redactor);
    }

    /// <summary>A Caravana with one app state; the deployment goes through the given statuses.</summary>
    private static FakeHandler Caravana(
        string? appRevision,
        Func<SeenRequest, HttpResponseMessage?>? deployAnswer = null,
        params string[] statuses
    )
    {
        var polls = 0;
        return new FakeHandler(r =>
        {
            if (r.Method == HttpMethod.Get && r.Path == "/v1/apps/shop")
                return appRevision is null
                    ? FakeHandler.Error(HttpStatusCode.NotFound, "not_found", "no such app")
                    : FakeHandler.Json(HttpStatusCode.OK, $$"""{"revision":"{{appRevision}}"}""");

            if (r.Method == HttpMethod.Post && r.Path == "/v1/apps/shop/compose")
                return deployAnswer?.Invoke(r)
                    ?? FakeHandler.Json(
                        HttpStatusCode.Accepted,
                        FakeHandler.Deployment("pending"),
                        "/v1/apps/shop/deployments/d1"
                    );

            if (r.Method == HttpMethod.Get && r.Path == "/v1/apps/shop/deployments/d1")
            {
                var status = statuses[Math.Min(polls++, statuses.Length - 1)];
                return FakeHandler.Json(HttpStatusCode.OK, FakeHandler.Deployment(status, status is "failed" or "reverted" or "superseded" ? "boom" : null));
            }

            return FakeHandler.Error(HttpStatusCode.NotFound, "not_found", $"unexpected {r}");
        });
    }

    // ── create vs update ─────────────────────────────────────────────────────

    [Fact]
    public async Task ANewApp_IsCreated_WithIfNoneMatch_AndTheBodyAndTheToken()
    {
        var handler = Caravana(null, null, "running", "healthy");
        var (flow, _, _) = NewFlow(handler);

        var deployment = await flow.RunAsync(Plan(), Ct);

        Assert.Equal("healthy", deployment.Status);
        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("/v1/apps/shop/compose", post.Path);
        Assert.Equal("*", post.Header("If-None-Match"));
        Assert.Null(post.Header("If-Match"));
        Assert.Equal($"Bearer {Token}", post.Header("Authorization"));

        using var body = JsonDocument.Parse(post.Body);
        Assert.Contains("image", body.RootElement.GetProperty("compose").GetString());
        Assert.Equal("DB_PASSWORD", body.RootElement.GetProperty("services").GetProperty("web").GetProperty("secrets")[0].GetProperty("name").GetString());
        // Default project and environment: nothing to create.
        Assert.DoesNotContain(handler.Requests, r => r.Path.StartsWith("/v1/projects"));
    }

    [Fact]
    public async Task AnExistingApp_IsUpdated_WithItsQuotedRevisionAsIfMatch()
    {
        var handler = Caravana("0123456789abcdef", null, "healthy");
        var (flow, _, _) = NewFlow(handler);

        await flow.RunAsync(Plan(), Ct);

        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("\"0123456789abcdef\"", post.Header("If-Match"));
        Assert.Null(post.Header("If-None-Match"));
    }

    [Fact]
    public async Task AStaleRevision_IsRetriedOnce_WithTheNewRevision()
    {
        var revision = "aaaaaaaaaaaaaaaa";
        var posts = 0;
        var handler = new FakeHandler(r =>
        {
            if (r.Method == HttpMethod.Get && r.Path == "/v1/apps/shop")
                return FakeHandler.Json(HttpStatusCode.OK, $$"""{"revision":"{{revision}}"}""");
            if (r.Method == HttpMethod.Post)
            {
                if (posts++ == 0)
                {
                    revision = "bbbbbbbbbbbbbbbb"; // someone saved in between
                    return FakeHandler.Error(HttpStatusCode.PreconditionFailed, "app_changed", "the app changed");
                }
                return FakeHandler.Json(HttpStatusCode.Accepted, FakeHandler.Deployment("healthy"), "/v1/apps/shop/deployments/d1");
            }
            return FakeHandler.Error(HttpStatusCode.NotFound, "not_found", "x");
        });
        var (flow, _, _) = NewFlow(handler);

        await flow.RunAsync(Plan(), Ct);

        var sent = handler.Requests.Where(r => r.Method == HttpMethod.Post).Select(r => r.Header("If-Match")).ToList();
        Assert.Equal(["\"aaaaaaaaaaaaaaaa\"", "\"bbbbbbbbbbbbbbbb\""], sent);
    }

    [Fact]
    public async Task ASecondConflict_IsReported_WithCodeAndMessage()
    {
        var handler = Caravana(
            "aaaaaaaaaaaaaaaa",
            _ => FakeHandler.Error(HttpStatusCode.PreconditionFailed, "app_changed", "the app changed"),
            "healthy"
        );
        var (flow, _, _) = NewFlow(handler);

        var ex = await Assert.ThrowsAsync<CaravanaDeployException>(() => flow.RunAsync(Plan(), Ct));

        Assert.StartsWith("app_changed: the app changed", ex.Message);
        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task ARaceOnCreate_IsRetriedAsAnUpdate()
    {
        var created = false;
        var handler = new FakeHandler(r =>
        {
            if (r.Method == HttpMethod.Get && r.Path == "/v1/apps/shop")
                return created
                    ? FakeHandler.Json(HttpStatusCode.OK, """{"revision":"cccccccccccccccc"}""")
                    : FakeHandler.Error(HttpStatusCode.NotFound, "not_found", "x");
            if (r.Method == HttpMethod.Post && r.Header("If-None-Match") == "*")
            {
                created = true;
                return FakeHandler.Error(HttpStatusCode.Conflict, "app_exists", "exists", "id");
            }
            return FakeHandler.Json(HttpStatusCode.Accepted, FakeHandler.Deployment("healthy"), "/v1/apps/shop/deployments/d1");
        });
        var (flow, _, _) = NewFlow(handler);

        await flow.RunAsync(Plan(), Ct);

        Assert.Equal("\"cccccccccccccccc\"", handler.Requests.Last(r => r.Method == HttpMethod.Post).Header("If-Match"));
    }

    // ── projects and environments ────────────────────────────────────────────

    [Fact]
    public async Task AMissingProjectAndEnvironment_AreCreated_BeforeTheApp()
    {
        var handler = new FakeHandler(r =>
        {
            return (r.Method.Method, r.Path) switch
            {
                ("GET", "/v1/projects/shop") => FakeHandler.Error(HttpStatusCode.NotFound, "project_not_found", "no"),
                ("POST", "/v1/projects") => FakeHandler.Json(HttpStatusCode.Created, """{"name":"shop","environments":[{"name":"production"}]}"""),
                ("POST", "/v1/projects/shop/environments") => FakeHandler.Json(HttpStatusCode.Created, """{"name":"staging"}"""),
                ("GET", "/v1/apps/shop--staging--shop") => FakeHandler.Error(HttpStatusCode.NotFound, "not_found", "x"),
                ("POST", "/v1/apps/shop--staging--shop/compose") => FakeHandler.Json(HttpStatusCode.Accepted, FakeHandler.Deployment("healthy"), "/v1/apps/shop--staging--shop/deployments/d1"),
                _ => FakeHandler.Error(HttpStatusCode.NotFound, "not_found", r.ToString()),
            };
        });
        var (flow, _, _) = NewFlow(handler);

        await flow.RunAsync(Plan("shop", "staging"), Ct);

        Assert.Equal(
            [
                "GET /v1/projects/shop",
                "POST /v1/projects",
                "POST /v1/projects/shop/environments",
                "GET /v1/apps/shop--staging--shop",
                "POST /v1/apps/shop--staging--shop/compose",
            ],
            handler.Requests.Select(r => r.ToString()).ToArray()
        );
        using var project = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("shop", project.RootElement.GetProperty("name").GetString());
        using var env = JsonDocument.Parse(handler.Requests[2].Body);
        Assert.Equal("staging", env.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task AProductionEnvironment_OfANewProject_IsNotCreatedTwice()
    {
        var handler = new FakeHandler(r =>
            (r.Method.Method, r.Path) switch
            {
                ("GET", "/v1/projects/shop") => FakeHandler.Error(HttpStatusCode.NotFound, "project_not_found", "no"),
                ("POST", "/v1/projects") => FakeHandler.Json(HttpStatusCode.Created, "{}"),
                ("GET", "/v1/apps/shop--production--shop") => FakeHandler.Error(HttpStatusCode.NotFound, "not_found", "x"),
                ("POST", "/v1/apps/shop--production--shop/compose") => FakeHandler.Json(HttpStatusCode.Accepted, FakeHandler.Deployment("healthy"), "/v1/apps/shop--production--shop/deployments/d1"),
                _ => FakeHandler.Error(HttpStatusCode.NotFound, "not_found", r.ToString()),
            }
        );
        var (flow, _, _) = NewFlow(handler);

        await flow.RunAsync(Plan("shop", "production"), Ct);

        Assert.DoesNotContain(handler.Requests, r => r.Path.EndsWith("/environments"));
    }

    [Fact]
    public async Task AnExistingProject_GetsOnlyTheMissingEnvironment()
    {
        var handler = new FakeHandler(r =>
            (r.Method.Method, r.Path) switch
            {
                ("GET", "/v1/projects/shop") => FakeHandler.Json(HttpStatusCode.OK, """{"environments":[{"name":"production"}]}"""),
                ("POST", "/v1/projects/shop/environments") => FakeHandler.Json(HttpStatusCode.Created, "{}"),
                ("GET", "/v1/apps/shop--staging--shop") => FakeHandler.Error(HttpStatusCode.NotFound, "not_found", "x"),
                ("POST", "/v1/apps/shop--staging--shop/compose") => FakeHandler.Json(HttpStatusCode.Accepted, FakeHandler.Deployment("healthy"), "/v1/apps/shop--staging--shop/deployments/d1"),
                _ => FakeHandler.Error(HttpStatusCode.NotFound, "not_found", r.ToString()),
            }
        );
        var (flow, _, _) = NewFlow(handler);

        await flow.RunAsync(Plan("shop", "staging"), Ct);

        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post && r.Path == "/v1/projects");
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Post && r.Path == "/v1/projects/shop/environments");
    }

    [Fact]
    public async Task ADeployToken_CantReadProjects_ButDeploysToOnesThatExist()
    {
        var handler = new FakeHandler(r =>
            (r.Method.Method, r.Path) switch
            {
                ("GET", "/v1/projects/shop") => FakeHandler.Error(HttpStatusCode.Forbidden, "token_scope", "this token can only deploy"),
                ("GET", "/v1/apps/shop--staging--shop") => FakeHandler.Error(HttpStatusCode.NotFound, "not_found", "x"),
                ("POST", "/v1/apps/shop--staging--shop/compose") => FakeHandler.Json(HttpStatusCode.Accepted, FakeHandler.Deployment("healthy"), "/v1/apps/shop--staging--shop/deployments/d1"),
                _ => FakeHandler.Error(HttpStatusCode.NotFound, "not_found", r.ToString()),
            }
        );
        var (flow, _, _) = NewFlow(handler);

        var deployment = await flow.RunAsync(Plan("shop", "staging"), Ct);

        Assert.Equal("healthy", deployment.Status);
    }

    [Fact]
    public async Task ADeployToken_ThatMeetsAMissingProject_GetsAClearMessage()
    {
        var handler = new FakeHandler(r =>
            (r.Method.Method, r.Path) switch
            {
                ("GET", "/v1/projects/shop") => FakeHandler.Error(HttpStatusCode.Forbidden, "token_scope", "this token can only deploy"),
                ("GET", "/v1/apps/shop--staging--shop") => FakeHandler.Error(HttpStatusCode.NotFound, "not_found", "x"),
                ("POST", "/v1/apps/shop--staging--shop/compose") => FakeHandler.Error(HttpStatusCode.NotFound, "project_not_found", "no project shop", "id"),
                _ => FakeHandler.Error(HttpStatusCode.NotFound, "not_found", r.ToString()),
            }
        );
        var (flow, _, _) = NewFlow(handler);

        var ex = await Assert.ThrowsAsync<CaravanaDeployException>(() => flow.RunAsync(Plan("shop", "staging"), Ct));

        Assert.Contains("project_not_found", ex.Message);
        Assert.Contains("create project 'shop' and environment 'staging' in Caravana first", ex.Message);
        Assert.Contains("deploy token", ex.Message);
    }

    // ── errors ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnErrorFromCaravana_IsReported_AsCodeMessageAndField()
    {
        var handler = Caravana(
            null,
            _ => FakeHandler.Error(HttpStatusCode.BadRequest, "invalid_field", "services.web.build is not supported", "services.web.build"),
            "healthy"
        );
        var (flow, _, _) = NewFlow(handler);

        var ex = await Assert.ThrowsAsync<CaravanaDeployException>(() => flow.RunAsync(Plan(), Ct));

        Assert.Equal("invalid_field: services.web.build is not supported (field: services.web.build)", ex.Message);
    }

    [Theory]
    [InlineData("image_not_found")]
    [InlineData("registry_auth_failed")]
    public async Task APrivateRegistryProblem_PointsAtSettingsRegistries(string code)
    {
        var handler = Caravana(null, _ => FakeHandler.Error(HttpStatusCode.UnprocessableEntity, code, "can't pull ghcr.io/acme/web:1"), "healthy");
        var (flow, _, _) = NewFlow(handler);

        var ex = await Assert.ThrowsAsync<CaravanaDeployException>(() => flow.RunAsync(Plan(), Ct));

        Assert.Contains("Settings > Registries", ex.Message);
        Assert.StartsWith(code + ":", ex.Message);
    }

    // ── waiting ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Healthy_EndsTheWait_AfterPolling()
    {
        var handler = Caravana(null, null, "pending", "running", "healthy");
        var (flow, _, _) = NewFlow(handler);

        await flow.RunAsync(Plan(), Ct);

        Assert.Equal(3, handler.Requests.Count(r => r.Path == "/v1/apps/shop/deployments/d1"));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("reverted")]
    [InlineData("superseded")]
    public async Task AFinalStatusThatIsNotHealthy_FailsWithTheReason(string status)
    {
        var handler = Caravana(null, null, "running", status);
        var (flow, _, _) = NewFlow(handler);

        var ex = await Assert.ThrowsAsync<CaravanaDeployException>(() => flow.RunAsync(Plan(), Ct));

        Assert.Contains(status, ex.Message);
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task TheWait_TimesOut()
    {
        var handler = Caravana(null, null, "running");
        var (flow, _, _) = NewFlow(handler);

        var ex = await Assert.ThrowsAsync<CaravanaDeployException>(() => flow.RunAsync(Plan(timeout: TimeSpan.FromMilliseconds(30)), Ct));

        Assert.Contains("still running", ex.Message);
    }

    [Fact]
    public async Task WithoutWaiting_TheAcceptedDeploymentIsReturned_NoPolling()
    {
        var handler = Caravana(null, null, "healthy");
        var (flow, _, _) = NewFlow(handler);

        var deployment = await flow.RunAsync(Plan(wait: false), Ct);

        Assert.Equal("pending", deployment.Status);
        Assert.DoesNotContain(handler.Requests, r => r.Path.Contains("/deployments/"));
    }

    [Fact]
    public async Task AFailedPoll_IsRetried_NotFatal()
    {
        var polls = 0;
        var handler = new FakeHandler(r =>
        {
            if (r.Method == HttpMethod.Get && r.Path == "/v1/apps/shop")
                return FakeHandler.Error(HttpStatusCode.NotFound, "not_found", "x");
            if (r.Method == HttpMethod.Post)
                return FakeHandler.Json(HttpStatusCode.Accepted, FakeHandler.Deployment("pending"), "/v1/apps/shop/deployments/d1");
            return polls++ == 0
                ? FakeHandler.Error(HttpStatusCode.BadGateway, "nomad_unavailable", "nomad is restarting")
                : FakeHandler.Json(HttpStatusCode.OK, FakeHandler.Deployment("healthy"));
        });
        var (flow, log, _) = NewFlow(handler);

        var deployment = await flow.RunAsync(Plan(), Ct);

        Assert.Equal("healthy", deployment.Status);
        Assert.Contains("trying again", log.All);
    }

    [Fact]
    public async Task APollThatIsRefused_IsNotRetried()
    {
        var handler = new FakeHandler(r =>
        {
            if (r.Method == HttpMethod.Get && r.Path == "/v1/apps/shop")
                return FakeHandler.Error(HttpStatusCode.NotFound, "not_found", "x");
            if (r.Method == HttpMethod.Post)
                return FakeHandler.Json(HttpStatusCode.Accepted, FakeHandler.Deployment("pending"), "/v1/apps/shop/deployments/d1");
            return FakeHandler.Error(HttpStatusCode.Unauthorized, "unauthorized", "no");
        });
        var (flow, _, _) = NewFlow(handler);

        await Assert.ThrowsAsync<CaravanaApiException>(() => flow.RunAsync(Plan(), Ct));
    }

    [Fact]
    public async Task TheWarnings_AreLogged()
    {
        var handler = Caravana(
            null,
            _ => FakeHandler.Json(
                HttpStatusCode.Accepted,
                FakeHandler.Deployment("healthy", null, """[{"field":"services.web.networks","message":"networks don't separate services here"}]"""),
                "/v1/apps/shop/deployments/d1"
            ),
            "healthy"
        );
        var (flow, log, _) = NewFlow(handler);

        await flow.RunAsync(Plan(), Ct);

        Assert.Contains("networks don't separate services here", log.All);
        Assert.Contains("services.web.networks", log.All);
    }

    // ── hygiene ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoSecretAndNoToken_AppearInLogsOrErrors_EvenWhenCaravanaEchoesThem()
    {
        // A misbehaving proxy or a future Caravana message that echoes the request.
        var handler = Caravana(
            null,
            _ => FakeHandler.Json(
                HttpStatusCode.BadRequest,
                $$$"""{"error":{"code":"invalid_field","message":"bad value {{{Secret}}} for DB_PASSWORD, token {{{Token}}}","field":"services.web.secrets"}}"""
            ),
            "healthy"
        );
        var (flow, log, _) = NewFlow(handler);

        var ex = await Assert.ThrowsAsync<CaravanaDeployException>(() => flow.RunAsync(Plan(), Ct));

        Assert.DoesNotContain(Secret, ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
        Assert.DoesNotContain(Secret, log.All);
        Assert.DoesNotContain(Token, log.All);
        Assert.Contains("***", ex.Message);
    }

    [Fact]
    public async Task ANonJsonErrorBody_IsRedactedAndTruncated()
    {
        var handler = Caravana(
            null,
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent($"<html>proxy error for {Token} {new string('x', 2000)}</html>"),
            },
            "healthy"
        );
        var (flow, _, _) = NewFlow(handler);

        var ex = await Assert.ThrowsAsync<CaravanaDeployException>(() => flow.RunAsync(Plan(), Ct));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.True(ex.Message.Length < 800);
    }

    [Fact]
    public async Task TheToken_IsOnlyEverInTheAuthorizationHeader()
    {
        var handler = Caravana("0123456789abcdef", null, "healthy");
        var (flow, _, _) = NewFlow(handler);

        await flow.RunAsync(Plan(), Ct);

        foreach (var request in handler.Requests)
        {
            Assert.DoesNotContain(Token, request.Path);
            Assert.DoesNotContain(Token, request.Body);
            Assert.Equal($"Bearer {Token}", request.Header("Authorization"));
            Assert.All(
                request.Message.Headers.Where(h => h.Key != "Authorization"),
                h => Assert.DoesNotContain(Token, string.Join(",", h.Value))
            );
        }
    }

    [Fact]
    public async Task ALocationOnAnotherHost_IsNeverFollowedWithTheToken()
    {
        var handler = Caravana(
            null,
            _ => FakeHandler.Json(HttpStatusCode.Accepted, FakeHandler.Deployment("pending"), "https://evil.example.com/v1/apps/shop/deployments/d1"),
            "healthy"
        );
        var (flow, _, _) = NewFlow(handler);

        await flow.RunAsync(Plan(), Ct);

        Assert.All(handler.Requests, r => Assert.Equal("caravana.test", r.Message.RequestUri!.Host));
    }

    // ── origin headers ───────────────────────────────────────────────────────

    [Fact]
    public async Task OriginHeaders_AreSentWithTheDeploy()
    {
        var origin = new Dictionary<string, string> { ["Caravana-Origin-Commit"] = "abc1234def", ["Caravana-Origin-Actor"] = "ana" };
        var handler = Caravana(null, null, "healthy");
        var (flow, _, _) = NewFlow(handler);

        await flow.RunAsync(Plan(origin: origin), Ct);

        var post = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("abc1234def", post.Header("Caravana-Origin-Commit"));
        Assert.Equal("ana", post.Header("Caravana-Origin-Actor"));
    }

    [Fact]
    public void OriginHeaders_FromGitHubActions()
    {
        var env = new Dictionary<string, string>
        {
            ["GITHUB_SHA"] = "ABC1234DEF5678ABC1234DEF5678ABC1234DEF56",
            ["GITHUB_REF"] = "refs/heads/main",
            ["GITHUB_ACTOR"] = "ana",
            ["GITHUB_REPOSITORY"] = "acme/shop",
            ["GITHUB_SERVER_URL"] = "https://github.com",
            ["GITHUB_RUN_ID"] = "212",
        };

        var headers = OriginHeaders.From(k => env.GetValueOrDefault(k));

        Assert.Equal("abc1234def5678abc1234def5678abc1234def56", headers["Caravana-Origin-Commit"]);
        Assert.Equal("refs/heads/main", headers["Caravana-Origin-Ref"]);
        Assert.Equal("ana", headers["Caravana-Origin-Actor"]);
        Assert.Equal("acme/shop", headers["Caravana-Origin-Repository"]);
        Assert.Equal("https://github.com/acme/shop/actions/runs/212", headers["Caravana-Origin-Run"]);
    }

    [Fact]
    public void OriginHeaders_OutsideCi_AreEmpty_AndABadPartIsLeftOut()
    {
        Assert.Empty(OriginHeaders.From(_ => null));

        var env = new Dictionary<string, string>
        {
            ["GITHUB_SHA"] = "not-a-sha",
            ["GITHUB_SERVER_URL"] = "http://ghe.local",
            ["GITHUB_REPOSITORY"] = "acme/shop",
            ["GITHUB_RUN_ID"] = "1",
            ["GITHUB_ACTOR"] = new string('a', 101),
        };
        var headers = OriginHeaders.From(k => env.GetValueOrDefault(k));

        Assert.False(headers.ContainsKey("Caravana-Origin-Commit"));
        Assert.False(headers.ContainsKey("Caravana-Origin-Run")); // https only
        Assert.False(headers.ContainsKey("Caravana-Origin-Actor")); // too long
        Assert.Equal("acme/shop", headers["Caravana-Origin-Repository"]);
    }

    // ── redactor ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheRedactor_MasksKnownValuesAndSecretLookingJsonProperties()
    {
        var redactor = new SecretRedactor();
        redactor.Add("hunter22");

        var text = redactor.Redact("""{"password":"other-value","value":"x","note":"hunter22 here","name":"keep"}""");

        Assert.DoesNotContain("other-value", text);
        Assert.DoesNotContain("hunter22", text);
        Assert.Contains("\"name\":\"keep\"", text);
        Assert.NotNull(JsonDocument.Parse(text)); // still well-formed
    }
}
