using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace CosmicChimps.Aspire.Hosting.Caravana;

/// <summary>
/// Marks a parameter whose value Caravana reads from a secret provider (Bella Baxter) when it deploys:
/// the publisher never holds the value. <see cref="Sentinel"/> stands in for it while Aspire writes the
/// Compose file and is replaced by a reference before anything is sent.
/// </summary>
internal sealed class CaravanaProviderSecretAnnotation(string provider, string path, string sentinel) : IResourceAnnotation
{
    /// <summary>The name of the secret provider in Caravana (Settings > Secret providers).</summary>
    public string Provider { get; } = provider;

    /// <summary>The path of the secret in the provider.</summary>
    public string Path { get; } = path;

    /// <summary>A random value for this run, such as <c>caravana-ref-</c> and 32 hex digits. Not a secret.</summary>
    public string Sentinel { get; } = sentinel;

    internal const string SentinelPrefix = "caravana-ref-";

    internal static string NewSentinel() => SentinelPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}

/// <summary>Secrets that Caravana reads from a secret provider.</summary>
public static partial class CaravanaProviderSecretExtensions
{
    /// <summary>The longest secret path Caravana takes, in bytes.</summary>
    internal const int MaxPathBytes = 512;

    /// <summary>
    /// Makes this parameter a reference to a secret in a Caravana secret provider (Bella Baxter): Caravana
    /// reads the value when it deploys, and its "Refresh secrets" rotates it later without a deploy. The
    /// publisher never holds, prompts for or sends the value, wherever the parameter is used: as a whole
    /// environment value (<c>KC_DB_PASSWORD</c>) or inside another one (a connection string).
    /// </summary>
    /// <remarks>
    /// While Aspire publishes, the parameter has a random per-run stand-in value, whatever the AppHost's
    /// configuration, user secrets or environment say, and Aspire never asks for it. The publisher replaces
    /// the stand-in by a reference. A parameter that is also mixed with a typed secret in one value, or used
    /// in a command, a label or any other field than an environment value, fails the deploy naming where.
    /// When the AppHost runs locally (not publishing) the parameter is left alone and resolves as usual.
    /// Needs a Caravana with secret providers (0.21.0), and one with secret templates when the parameter is
    /// part of a longer value: an older Caravana refuses the deploy before anything is sent.
    /// </remarks>
    /// <param name="builder">The parameter, marked <c>secret: true</c> or not.</param>
    /// <param name="provider">The provider's name in Caravana (Settings > Secret providers): a lowercase DNS label such as <c>bella-prod</c>.</param>
    /// <param name="path">The secret's path in the provider, given as it is there: 1 to 512 bytes, no control characters.
    /// It is never taken from the parameter's name (Bella refuses '-', which Aspire parameter names use).</param>
    /// <example>
    /// <code>
    /// var dbPassword = builder.AddParameter("db-password", secret: true)
    ///     .AsCaravanaProviderSecret("bella-prod", "DB_PASSWORD");
    /// </code>
    /// </example>
    public static IResourceBuilder<ParameterResource> AsCaravanaProviderSecret(
        this IResourceBuilder<ParameterResource> builder,
        string provider,
        string path
    )
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(path);
        if (!ProviderPattern().IsMatch(provider))
            throw new ArgumentException(
                "A provider is its name in Caravana: a lowercase DNS label (1 to 63 lowercase letters, digits and '-', starting and ending with a letter or digit), such as bella-prod.",
                nameof(provider)
            );
        if (path.Length == 0 || Encoding.UTF8.GetByteCount(path) > MaxPathBytes)
            throw new ArgumentException($"A secret's path is 1 to {MaxPathBytes} bytes.", nameof(path));
        if (path.Any(char.IsControl))
            throw new ArgumentException("A secret's path has no control characters.", nameof(path));

        var parameter = builder.Resource;
        if (parameter.Annotations.OfType<CaravanaProviderSecretAnnotation>().FirstOrDefault() is { } existing)
        {
            if (existing.Provider == provider && existing.Path == path)
                return builder;
            throw new InvalidOperationException(
                $"Parameter '{parameter.Name}' is already a reference to '{existing.Path}' in provider '{existing.Provider}'; "
                    + "one parameter can't be two secrets."
            );
        }

        var annotation = new CaravanaProviderSecretAnnotation(provider, path, CaravanaProviderSecretAnnotation.NewSentinel());
        parameter.Annotations.Add(annotation);

        // Publishing is where the stand-in goes: a deploy and a publish read the parameter, and the
        // sentinel must be there for them. When the AppHost runs locally the parameter is the developer's own.
        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
            PinValue(parameter, annotation.Sentinel);
        return builder;
    }

    /// <summary>
    /// Makes the parameter's value the sentinel, whatever configuration, user secrets or the environment say,
    /// without a prompt: Aspire asks the user only when the parameter's getter throws, and this one doesn't.
    /// <see cref="ParameterResource"/> takes its getter in the constructor and has no way to change it, and the
    /// parameter is already in the model (references to it are held by other resources), so the two fields are
    /// replaced. It fails loudly if Aspire's internals change, and a test pins the Aspire version it was written for.
    /// </summary>
    private static void PinValue(ParameterResource parameter, string sentinel)
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var getter = typeof(ParameterResource).GetField("_valueGetter", Fields);
        var lazy = typeof(ParameterResource).GetField("_lazyValue", Fields);
        if (getter is null || lazy is null || getter.FieldType != typeof(Func<ParameterDefault?, string>) || lazy.FieldType != typeof(Lazy<string>))
            throw new InvalidOperationException(
                "This version of Aspire keeps a parameter's value somewhere AsCaravanaProviderSecret doesn't know, so it can't "
                    + "guarantee that the real value is never read. Update CosmicChimps.Aspire.Hosting.Caravana to the release for your Aspire version."
            );

        getter.SetValue(parameter, new Func<ParameterDefault?, string>(_ => sentinel));
        lazy.SetValue(parameter, new Lazy<string>(() => sentinel));
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex ProviderPattern();
}
