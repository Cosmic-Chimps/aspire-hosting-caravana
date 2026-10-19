using System.Text.RegularExpressions;

namespace CosmicChimps.Aspire.Hosting.Caravana;

/// <summary>
/// Masks the token and every secret value in text that is about to be logged or thrown, such as an
/// error body from Caravana. Known values are masked wherever they appear; JSON properties whose
/// name looks secret are masked whatever their value.
/// </summary>
internal sealed partial class SecretRedactor
{
    private readonly List<string> _secrets = [];

    internal const string Mask = "***";

    /// <summary>Adds values to mask. Empty values are ignored (they would match everywhere).</summary>
    internal void Add(IEnumerable<string?> values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrEmpty(value) && !_secrets.Contains(value))
                _secrets.Add(value);
        }
        // Longest first, so a secret that contains another is masked whole.
        _secrets.Sort((a, b) => b.Length.CompareTo(a.Length));
    }

    internal void Add(string? value) => Add([value]);

    internal string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var result = SecretJsonProperty().Replace(text, "$1\"" + Mask + "\"");
        foreach (var secret in _secrets)
            result = result.Replace(secret, Mask, StringComparison.Ordinal);
        return result;
    }

    // "value": "x" in a secrets entry, and any property that names a password, token or key.
    [GeneratedRegex(
        """("(?:value|[^"]*(?:password|passwd|token|secret|apikey|api_key|signingkey)[^"]*)"\s*:\s*)"[^"]*["]""",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex SecretJsonProperty();
}
