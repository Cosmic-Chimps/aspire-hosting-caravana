namespace CosmicChimps.Aspire.Hosting.Caravana;

/// <summary>
/// The Caravana releases that first understood what the publisher sends. Caravana's API ignores
/// fields it doesn't know, so an older Caravana would take a reference as a secret with no value and
/// say nothing: the publisher reads the version from <c>GET /v1/health</c> and refuses first.
/// </summary>
internal static class CaravanaVersions
{
    /// <summary>
    /// The first release with secret providers (<c>from</c> on a secret, API 0.40.0): Caravana 0.21.0.
    /// </summary>
    internal static readonly Version MinForProviderReferences = new(0, 21, 0);

    /// <summary>
    /// The first release with secret templates (<c>template</c> and <c>refs</c> on a secret, API 0.59.0,
    /// Caravana feature 053).
    /// </summary>
    // TODO: set this to the Caravana release that ships feature 053 before this package is released. 0.23.0
    // is a guess (the release after 0.22.0): a Caravana between 0.22.0 and the real one would pass the
    // check and silently drop the template.
    internal static readonly Version MinForSecretTemplates = new(0, 23, 0);

    /// <summary>
    /// Reads <c>major.minor.patch</c> from the start of a version text (a leading <c>v</c> and anything after
    /// the patch number, such as <c>-rc.1</c>, are ignored). False for <c>dev</c> or anything else.
    /// </summary>
    internal static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.Trim().TrimStart('v', 'V');
        var end = 0;
        var dots = 0;
        while (end < trimmed.Length && (char.IsAsciiDigit(trimmed[end]) || (trimmed[end] == '.' && dots++ < 2)))
            end++;
        if (!Version.TryParse(trimmed[..end], out var parsed) || parsed.Build < 0)
            return false;
        version = new Version(parsed.Major, parsed.Minor, parsed.Build);
        return true;
    }

    /// <summary>
    /// True when <paramref name="running"/> is at least <paramref name="minimum"/>. A text that isn't a
    /// version (a <c>dev</c> build) is allowed: it can't be told, and a developer's own build is usually newest.
    /// </summary>
    internal static bool IsAtLeast(string? running, Version minimum) =>
        !TryParse(running, out var version) || version >= minimum;
}
