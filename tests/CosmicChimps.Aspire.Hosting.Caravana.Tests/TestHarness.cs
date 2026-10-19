using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CosmicChimps.Aspire.Hosting.Caravana.Tests;

/// <summary>A request the fake handler saw, with its body read before the request was disposed.</summary>
internal sealed record SeenRequest(HttpMethod Method, string Path, HttpRequestMessage Message, string Body)
{
    internal string? Header(string name) =>
        Message.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    public override string ToString() => $"{Method} {Path}";
}

/// <summary>An <see cref="HttpMessageHandler"/> that answers from a function and remembers every request.</summary>
internal sealed class FakeHandler(Func<SeenRequest, HttpResponseMessage> respond) : HttpMessageHandler
{
    internal List<SeenRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var seen = new SeenRequest(request.Method, request.RequestUri!.PathAndQuery, request, body);
        Requests.Add(seen);
        return respond(seen);
    }

    internal HttpClient Client() => new(this) { BaseAddress = new Uri("https://caravana.test/") };

    internal static HttpResponseMessage Json(HttpStatusCode status, string json, string? location = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (location is not null)
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    internal static HttpResponseMessage Error(HttpStatusCode status, string code, string message, string? field = null)
    {
        var error = new Dictionary<string, string> { ["code"] = code, ["message"] = message };
        if (field is not null)
            error["field"] = field;
        return Json(status, System.Text.Json.JsonSerializer.Serialize(new { error }));
    }

    internal static string Deployment(string status, string? error = null, string warnings = "[]")
    {
        var errorPart = error is null ? "" : "," + System.Text.Json.JsonSerializer.Serialize("error") + ":" + System.Text.Json.JsonSerializer.Serialize(error);
        return "{\"id\":\"d1\",\"appId\":\"shop\",\"status\":\"" + status + "\"" + errorPart + ",\"warnings\":" + warnings + "}";
    }
}

/// <summary>An <see cref="ILogger"/> that keeps every formatted message, for "nothing secret was logged" checks.</summary>
internal sealed class ListLogger : ILogger
{
    internal List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Messages.Add($"{logLevel}: {formatter(state, exception)}");
        if (exception is not null)
            Messages.Add($"{logLevel}: {exception}");
    }

    internal string All => string.Join("\n", Messages);
}
