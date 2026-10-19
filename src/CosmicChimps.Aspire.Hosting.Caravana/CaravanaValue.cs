using Aspire.Hosting.ApplicationModel;

namespace CosmicChimps.Aspire.Hosting.Caravana;

/// <summary>
/// A setting that is either known when the application model is built (a literal string) or only
/// when the deployment runs (an Aspire <see cref="ParameterResource"/> or any other
/// <see cref="IValueProvider"/>).
/// </summary>
/// <remarks>
/// Deployment settings such as Caravana's address and token belong in Aspire parameters: they are
/// prompted for when missing, can be marked secret and can differ per environment. Both forms are
/// accepted by assignment: <c>settings.Url = "https://caravana.example.com"</c> or
/// <c>settings.Token = token.AsCaravanaValue()</c>. Resolution happens once, when the deploy step
/// runs, never while the model is built.
/// </remarks>
public sealed class CaravanaValue
{
    private readonly string? _literal;
    private readonly IValueProvider? _provider;
    private readonly string? _providerName;

    private CaravanaValue(string literal) => _literal = literal;

    private CaravanaValue(IValueProvider provider, string? providerName)
    {
        _provider = provider;
        _providerName = providerName;
    }

    /// <summary>True when this holds a deferred value that has not been resolved yet.</summary>
    public bool IsDeferred => _provider is not null;

    /// <summary>Wraps a literal string.</summary>
    public static CaravanaValue FromLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new CaravanaValue(value);
    }

    /// <summary>Wraps an Aspire parameter, resolved when the deployment runs.</summary>
    public static CaravanaValue FromParameter(IResourceBuilder<ParameterResource> parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return new CaravanaValue(parameter.Resource, parameter.Resource.Name);
    }

    /// <summary>Wraps any value provider (a parameter, a reference expression, an endpoint).</summary>
    public static CaravanaValue FromValueProvider(IValueProvider provider, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new CaravanaValue(provider, name);
    }

    public static implicit operator CaravanaValue?(string? value) =>
        value is null ? null : new CaravanaValue(value);

    public static implicit operator CaravanaValue(ParameterResource parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return new CaravanaValue(parameter, parameter.Name);
    }

    /// <summary>Resolves the value. Call while deploying, not while building the model.</summary>
    public async ValueTask<string?> GetValueAsync(CancellationToken ct = default) =>
        _provider is not null ? await _provider.GetValueAsync(ct).ConfigureAwait(false) : _literal;

    /// <summary>
    /// Safe for logs: a deferred value renders as its parameter name, and a literal as
    /// <c>&lt;literal&gt;</c>. A literal may be the token itself, so it is never printed.
    /// </summary>
    public override string ToString() =>
        _provider is not null ? $"<parameter:{_providerName ?? "unnamed"}>" : "<literal>";
}

/// <summary>Turns an Aspire parameter builder into a <see cref="CaravanaValue"/>.</summary>
/// <remarks>
/// C# forbids user-defined conversions from an interface type, and <c>AddParameter</c> returns
/// <c>IResourceBuilder&lt;ParameterResource&gt;</c>, so assign with this (or with <c>.Resource</c>).
/// </remarks>
public static class CaravanaParameterExtensions
{
    /// <summary>Wraps the parameter so it resolves when the deployment runs.</summary>
    public static CaravanaValue AsCaravanaValue(this IResourceBuilder<ParameterResource> parameter) =>
        CaravanaValue.FromParameter(parameter);
}

internal static class CaravanaValueExtensions
{
    internal static async ValueTask<string?> ResolveAsync(this CaravanaValue? value, CancellationToken ct) =>
        value is null ? null : await value.GetValueAsync(ct).ConfigureAwait(false);

    internal static async ValueTask<string> ResolveOrDefaultAsync(
        this CaravanaValue? value,
        string fallback,
        CancellationToken ct
    )
    {
        var resolved = await value.ResolveAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(resolved) ? fallback : resolved.Trim();
    }

    internal static async ValueTask<string> ResolveRequiredAsync(
        this CaravanaValue? value,
        string settingName,
        CancellationToken ct
    )
    {
        var resolved = await value.ResolveAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(resolved)
            ? throw new InvalidOperationException(
                $"Caravana setting '{settingName}' resolved to an empty value. Set it to a literal "
                    + "string or an Aspire parameter (builder.AddParameter(...))."
            )
            : resolved.Trim();
    }
}
