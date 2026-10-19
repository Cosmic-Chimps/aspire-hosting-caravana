using System.Text.RegularExpressions;

namespace CosmicChimps.Aspire.Hosting.Caravana;

/// <summary>
/// The <c>Caravana-Origin-*</c> headers that tell Caravana a deploy came from CI, taken from the
/// <c>GITHUB_*</c> variables. A part that would break Caravana's rule for its header is left out: Caravana
/// answers 422 for those, and a missing "from CI" label is better than a failed deploy.
/// </summary>
internal static partial class OriginHeaders
{
    [GeneratedRegex("^[0-9a-fA-F]{7,64}$")]
    private static partial Regex CommitPattern();

    [GeneratedRegex(@"[\p{C}]")]
    private static partial Regex ControlChars();

    internal static IReadOnlyDictionary<string, string> From(Func<string, string?> env)
    {
        var headers = new Dictionary<string, string>();

        var sha = Clean(env("GITHUB_SHA"));
        if (sha is not null && CommitPattern().IsMatch(sha))
            headers["Caravana-Origin-Commit"] = sha.ToLowerInvariant();

        Add(headers, "Caravana-Origin-Ref", Clean(env("GITHUB_REF")), 255);
        Add(headers, "Caravana-Origin-Actor", Clean(env("GITHUB_ACTOR")), 100);
        Add(headers, "Caravana-Origin-Repository", Clean(env("GITHUB_REPOSITORY")), 200);

        var server = Clean(env("GITHUB_SERVER_URL"))?.TrimEnd('/');
        var repository = Clean(env("GITHUB_REPOSITORY"));
        var runId = Clean(env("GITHUB_RUN_ID"));
        if (server is not null && repository is not null && runId is not null)
        {
            var run = $"{server}/{repository}/actions/runs/{runId}";
            if (run.StartsWith("https://", StringComparison.Ordinal))
                Add(headers, "Caravana-Origin-Run", run, 500);
        }

        return headers;
    }

    private static void Add(Dictionary<string, string> headers, string name, string? value, int max)
    {
        if (value is not null && value.Length <= max)
            headers[name] = value;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return ControlChars().IsMatch(trimmed) ? null : trimmed;
    }
}
