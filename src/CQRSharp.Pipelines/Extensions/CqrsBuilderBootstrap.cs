using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Pipelines;

/// <summary>
///     The builder half of the source-generated <c>AddCqrsGenerated(Action&lt;ICqrsBuilder&gt;)</c>. Call that entry point,
///     not this type: it applies the source-generated handler routing after this runs, and without it no request reaches
///     its handler.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class CqrsBuilderBootstrap
{
    /// <summary>
    ///     Runs <paramref name="configure" /> against a fresh <see cref="ICqrsBuilder" /> over <paramref name="services" />
    ///     and applies what it recorded: the core services, the configured options and stores, and the pipeline behaviors.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configure">The builder configuration.</param>
    /// <exception cref="ArgumentNullException">
    ///     Thrown when <paramref name="services" /> or <paramref name="configure" /> is <see langword="null" />.
    /// </exception>
    public static void Build(IServiceCollection services, Action<ICqrsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new CqrsBuilder(services);
        configure(builder);
        builder.Build();
    }
}
