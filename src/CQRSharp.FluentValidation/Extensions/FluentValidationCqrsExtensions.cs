using System.Diagnostics.CodeAnalysis;
using CQRSharp;
using CQRSharp.FluentValidation;
using CQRSharp.Pipelines;
using Microsoft.Extensions.DependencyInjection.Extensions;

// Namespace-extends the DI builder so registration reads naturally next to the rest of the app's service wiring.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Registration helpers that plug FluentValidation validators into CQRSharp's validation behavior.
///     FluentValidation is not Native-AOT/full-trim compatible — these verbs carry the corresponding analyzer
///     annotations.
/// </summary>
public static class FluentValidationCqrsExtensions
{
    private const string AotMessage =
        "FluentValidation compiles expression trees at runtime and is not compatible with Native AOT or full trimming.";

    /// <summary>
    ///     Registers <see cref="FluentValidationRequestValidator{TRequest}" /> as an open-generic
    ///     <see cref="IRequestValidator{TRequest}" />, so every FluentValidation <c>IValidator&lt;TRequest&gt;</c> in the
    ///     container runs inside CQRSharp's validation behavior, next to any native validators. Safe to call more than
    ///     once. This method does NOT register your validators: pair it with FluentValidation's
    ///     <c>AddValidatorsFromAssemblyContaining&lt;T&gt;()</c> or register them by hand.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <paramref name="services" /> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services" /> is <see langword="null" />.</exception>
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public static IServiceCollection AddCqrsFluentValidation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Transient, like the validation behavior that consumes it: the adapter captures the request's validators,
        // which are commonly scoped, so it must never outlive the scope it was resolved from.
        services.TryAddEnumerable(ServiceDescriptor.Transient(
            typeof(IRequestValidator<>),
            typeof(FluentValidationRequestValidator<>)));

        return services;
    }

    /// <summary>
    ///     Runs FluentValidation validators in the CQRSharp pipeline by registering the adapter (see
    ///     <see cref="AddCqrsFluentValidation" />). The builder's validation behavior, on unless
    ///     <c>UseValidation(false)</c> is called, runs them next to any native validators. It does not scan for validators;
    ///     register them with FluentValidation's <c>AddValidatorsFromAssemblyContaining&lt;T&gt;()</c> or by hand.
    /// </summary>
    /// <remarks>
    ///     Unlike the builder's own verbs, the adapter registration is applied to <see cref="ICqrsBuilder.Services" />
    ///     immediately. <c>UseValidation(false)</c> turns FluentValidation validators off together with every other
    ///     validator, whatever the order of the two calls.
    /// </remarks>
    /// <param name="builder">The CQRSharp builder.</param>
    /// <returns>The same <paramref name="builder" /> for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder" /> is <see langword="null" />.</exception>
    [RequiresDynamicCode(AotMessage)]
    [RequiresUnreferencedCode(AotMessage)]
    public static ICqrsBuilder UseFluentValidation(this ICqrsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddCqrsFluentValidation();
        return builder;
    }
}
