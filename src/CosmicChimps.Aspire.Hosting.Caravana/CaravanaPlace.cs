using System.Text.RegularExpressions;

namespace CosmicChimps.Aspire.Hosting.Caravana;

/// <summary>
/// Where an app lives in Caravana, and the app ID that place makes. Mirrors Caravana's own rule
/// (<c>internal/app/place.go</c>): the bare name in <c>default</c>/<c>production</c>, otherwise
/// <c>&lt;project&gt;--&lt;environment&gt;--&lt;name&gt;</c>.
/// </summary>
internal static partial class CaravanaPlace
{
    internal const string DefaultProject = "default";
    internal const string DefaultEnvironment = "production";
    private const string Separator = "--";

    /// <summary>A lowercase DNS label with single dashes, at most 63 characters (Caravana's <c>Name</c>).</summary>
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex NameRegex();

    internal static bool IsValidName(string name) =>
        name.Length is > 0 and <= 63 && NameRegex().IsMatch(name);

    /// <summary>The ID of the app in this place. Names must be valid (see <see cref="Validate"/>).</summary>
    internal static string AppId(string project, string environment, string name) =>
        project == DefaultProject && environment == DefaultEnvironment
            ? name
            : string.Join(Separator, project, environment, name);

    /// <summary>The problems with the three names, each naming the setting. Empty when all are fine.</summary>
    internal static IReadOnlyList<string> Validate(string project, string environment, string name)
    {
        var problems = new List<string>();
        foreach (var (setting, value) in new[] { ("Project", project), ("Environment", environment), ("AppName", name) })
        {
            if (!IsValidName(value))
                problems.Add(
                    $"{setting} '{value}' is not a Caravana name: lowercase letters, numbers and single dashes, "
                        + "at most 63 characters."
                );
        }
        return problems;
    }
}
