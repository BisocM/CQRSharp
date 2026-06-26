using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Pipelines.Extensions;

/// <summary>
///     The fluent entry point that does not wire the source-generated registrations. Use this when the generated
///     registrations are applied elsewhere (e.g. a separate <c>AddCqrsGenerated()</c> call); the generated fluent
///     overload <c>AddCqrsGenerated(Action&lt;ICqrsBuilder&gt;)</c> is the entry point that also wires them.
/// </summary>
public static class CqrsBuilderExtensions
{
    /// <summary>
    ///     Configures CQRSharp through the order-insensitive fluent builder. The delegate is non-optional so this
    ///     overload is unambiguous against the existing
    ///     <c>AddCqrs(Action&lt;BackgroundTaskQueueOptions&gt;?, ...)</c> overload — <c>AddCqrs(b =&gt; ...)</c> always
    ///     binds here. In this path <see cref="ICqrsBuilder.UseGenerated" /> is a no-op (the generated registrations
    ///     are not applied by this entry point); apply them with a separate <c>AddCqrsGenerated()</c> call.
    /// </summary>
    public static IServiceCollection AddCqrs(this IServiceCollection services, Action<ICqrsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new CqrsBuilder(services);
        configure(builder);
        return builder.Build();
    }
}
