using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CosmicChimps.Aspire.Hosting.Caravana.Models;

namespace CosmicChimps.Aspire.Hosting.Caravana;

/// <summary>A failed Caravana API call: the HTTP status and the error body's code, message and field.</summary>
internal sealed class CaravanaApiException(int status, string? code, string message, string? field)
    : Exception(message)
{
    internal int Status { get; } = status;

    /// <summary>The stable error code (<c>app_changed</c>, <c>image_not_found</c>, ...), when the body had one.</summary>
    internal string? Code { get; } = code;

    /// <summary>The request field at fault, as a path like <c>services[0].backup.engine</c>.</summary>
    internal string? Field { get; } = field;

    /// <summary><c>code: message (field)</c>, as the step reports it.</summary>
    internal string Describe() =>
        (Code is null ? $"HTTP {Status}" : Code) + ": " + Message + (string.IsNullOrEmpty(Field) ? "" : $" (field: {Field})");
}

internal sealed record WarningInfo(string Field, string Message);

/// <summary>Where a deploy came from, as Caravana's <c>Deployment.origin</c> says. Every part is optional.</summary>
internal sealed record OriginInfo(string? Commit, string? Actor, string? Run);

/// <summary>The parts of a Caravana <c>Deployment</c> the publisher uses.</summary>
internal sealed record DeploymentInfo(
    string Id,
    string Status,
    string? Error,
    IReadOnlyList<WarningInfo> Warnings,
    string? Trigger = null,
    int? JobVersion = null,
    OriginInfo? Origin = null
)
{
    internal bool IsFinal => Status is "healthy" or "failed" or "reverted" or "superseded";
}

internal sealed record AppInfo(string Revision);

internal sealed record ProjectInfo(IReadOnlyList<string> Environments);

internal sealed record AcceptedDeployment(DeploymentInfo Deployment, string Location);

/// <summary>
/// The few Caravana API calls the publisher makes. The token travels only in the
/// <c>Authorization</c> header; every error text passes through the redactor first.
/// </summary>
internal sealed class CaravanaApiClient(HttpClient http, string token, SecretRedactor redactor)
{
    private const int MaxBodyChars = 500;

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    internal async Task<ProjectInfo?> GetProjectAsync(string project, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/v1/projects/{Uri.EscapeDataString(project)}", null, NoHeaders, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);

        using var doc = await ReadJsonAsync(response, ct);
        var environments = new List<string>();
        if (doc.RootElement.TryGetProperty("environments", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var env in list.EnumerateArray())
            {
                var envName = env.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                if (envName is not null)
                    environments.Add(envName);
            }
        }
        return new ProjectInfo(environments);
    }

    internal async Task CreateProjectAsync(string project, string? displayName, CancellationToken ct)
    {
        var body = new Dictionary<string, string> { ["name"] = project };
        if (!string.IsNullOrWhiteSpace(displayName))
            body["displayName"] = displayName;
        using var response = await SendAsync(HttpMethod.Post, "/v1/projects", body, NoHeaders, ct);
        await EnsureSuccessAsync(response, ct);
    }

    internal async Task CreateEnvironmentAsync(string project, string environment, CancellationToken ct)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/v1/projects/{Uri.EscapeDataString(project)}/environments",
            new { name = environment },
            NoHeaders,
            ct
        );
        await EnsureSuccessAsync(response, ct);
    }

    internal async Task<AppInfo?> GetAppAsync(string appId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/v1/apps/{Uri.EscapeDataString(appId)}", null, NoHeaders, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, ct);

        using var doc = await ReadJsonAsync(response, ct);
        var revision = doc.RootElement.TryGetProperty("revision", out var r) ? r.GetString() : null;
        revision ??= response.Headers.ETag?.Tag.Trim('"');
        if (string.IsNullOrEmpty(revision))
            throw new CaravanaApiException((int)response.StatusCode, null, "Caravana's answer for the app has no revision.", null);
        return new AppInfo(revision);
    }

    /// <summary>
    /// <c>POST /v1/apps/{id}/compose</c>. With <paramref name="ifMatchRevision"/> it updates the app only if
    /// the stored revision still is that one (<c>If-Match</c>); without it, it creates the app only if the
    /// ID is free (<c>If-None-Match: *</c>).
    /// </summary>
    internal async Task<AcceptedDeployment> DeployComposeAsync(
        string appId,
        ComposeRequest body,
        string? ifMatchRevision,
        IReadOnlyDictionary<string, string> originHeaders,
        CancellationToken ct
    )
    {
        var path = $"/v1/apps/{Uri.EscapeDataString(appId)}/compose";
        using var request = NewRequest(HttpMethod.Post, path, body, originHeaders);
        if (ifMatchRevision is null)
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        else
            request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{ifMatchRevision}\""));

        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        using var doc = await ReadJsonAsync(response, ct);
        var deployment = ParseDeployment(doc.RootElement);
        var location = SafeLocation(response.Headers.Location, appId, deployment.Id);
        return new AcceptedDeployment(deployment, location);
    }

    internal async Task<DeploymentInfo> GetDeploymentAsync(string location, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, location, null, NoHeaders, ct);
        await EnsureSuccessAsync(response, ct);
        using var doc = await ReadJsonAsync(response, ct);
        return ParseDeployment(doc.RootElement);
    }

    /// <summary>
    /// <c>GET /v1/apps/{id}/deployments</c>: the app's newest deployments, newest first.
    /// </summary>
    internal async Task<IReadOnlyList<DeploymentInfo>> ListDeploymentsAsync(string appId, int limit, CancellationToken ct)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/v1/apps/{Uri.EscapeDataString(appId)}/deployments?limit={limit}",
            null,
            NoHeaders,
            ct
        );
        await EnsureSuccessAsync(response, ct);
        using var doc = await ReadJsonAsync(response, ct);

        var deployments = new List<DeploymentInfo>();
        if (doc.RootElement.TryGetProperty("deployments", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
                deployments.Add(ParseDeployment(item));
        }
        return deployments;
    }

    // The token is sent to whatever address this returns, so only the path of Location is used:
    // never a different host.
    private static string SafeLocation(Uri? location, string appId, string deploymentId)
    {
        if (location is not null)
            return location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
        return $"/v1/apps/{Uri.EscapeDataString(appId)}/deployments/{Uri.EscapeDataString(deploymentId)}";
    }

    internal static DeploymentInfo ParseDeployment(JsonElement root)
    {
        var warnings = new List<WarningInfo>();
        if (root.TryGetProperty("warnings", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in list.EnumerateArray())
            {
                warnings.Add(
                    new WarningInfo(
                        w.TryGetProperty("field", out var f) ? f.GetString() ?? "" : "",
                        w.TryGetProperty("message", out var m) ? m.GetString() ?? "" : ""
                    )
                );
            }
        }

        return new DeploymentInfo(
            root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            root.TryGetProperty("status", out var status) ? status.GetString() ?? "" : "",
            root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null,
            warnings,
            root.TryGetProperty("trigger", out var trigger) && trigger.ValueKind == JsonValueKind.String ? trigger.GetString() : null,
            root.TryGetProperty("jobVersion", out var version) && version.TryGetInt32(out var v) ? v : null,
            ParseOrigin(root)
        );
    }

    private static OriginInfo? ParseOrigin(JsonElement root)
    {
        if (!root.TryGetProperty("origin", out var origin) || origin.ValueKind != JsonValueKind.Object)
            return null;

        string? Text(string name) =>
            origin.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        return new OriginInfo(Text("commit"), Text("actor"), Text("run"));
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    private HttpRequestMessage NewRequest(
        HttpMethod method,
        string path,
        object? body,
        IReadOnlyDictionary<string, string> extraHeaders
    )
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        foreach (var (name, value) in extraHeaders)
            request.Headers.TryAddWithoutValidation(name, value);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, body.GetType(), CaravanaJson.Options),
                Encoding.UTF8,
                "application/json"
            );
        }
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        IReadOnlyDictionary<string, string> extraHeaders,
        CancellationToken ct
    )
    {
        using var request = NewRequest(method, path, body, extraHeaders);
        return await http.SendAsync(request, ct);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync<JsonDocument>(ct)
        ?? throw new CaravanaApiException((int)response.StatusCode, null, "Caravana answered with an empty body.", null);

    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        throw await ToExceptionAsync(response, ct);
    }

    internal async Task<CaravanaApiException> ToExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct);
        string? code = null,
            field = null;
        var message = (string?)null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                field = error.TryGetProperty("field", out var f) ? f.GetString() : null;
            }
        }
        catch (JsonException)
        {
            // Not Caravana's error shape (a proxy's page, say): fall through to the raw text.
        }

        message ??= raw.Length == 0 ? response.ReasonPhrase ?? "no message" : Truncate(raw);
        return new CaravanaApiException(
            (int)response.StatusCode,
            code,
            redactor.Redact(message),
            field is null ? null : redactor.Redact(field)
        );
    }

    private static string Truncate(string text) =>
        text.Length <= MaxBodyChars ? text : text[..MaxBodyChars] + "...";
}
