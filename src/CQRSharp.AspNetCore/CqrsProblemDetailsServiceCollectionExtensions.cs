using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CQRSharp.AspNetCore;

/// <summary>
///     Registers the CQRSharp exception-to-ProblemDetails mapping.
/// </summary>
public static class CqrsProblemDetailsServiceCollectionExtensions
{
    /// <summary>
    ///     Registers an <see cref="Microsoft.AspNetCore.Diagnostics.IExceptionHandler" /> that maps the known,
    ///     user-safe CQRSharp exceptions to RFC 7807 ProblemDetails responses:
    ///     <see cref="RequestValidationException" /> to a <c>400</c> validation problem,
    ///     <see cref="DuplicateRequestException" /> to <c>409</c>,
    ///     <see cref="CQRSharp.Pipelines.RateLimitExceededException" /> to <c>429</c>,
    ///     <see cref="TimeoutException" /> to <c>504</c> and <see cref="InvalidIdempotencyKeyException" /> to
    ///     <c>400</c>. Every other exception is left unhandled.
    /// </summary>
    /// <remarks>
    ///     Also calls <c>AddProblemDetails()</c> (idempotent), so responses go through the host's
    ///     <see cref="Microsoft.AspNetCore.Http.IProblemDetailsService" /> and any <c>CustomizeProblemDetails</c>
    ///     callback applies. The handler only runs when the exception-handler middleware is in the pipeline: call
    ///     <c>app.UseExceptionHandler()</c>.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services" /> is <see langword="null" />.</exception>
    public static IServiceCollection AddCqrsProblemDetails(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<CqrsProblemDetailsOptions>();
        services.AddProblemDetails();
        // AddExceptionHandler<T> appends unconditionally; TryAddEnumerable keeps a repeated call from running the
        // handler twice per exception.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionHandler, CqrsExceptionHandler>());
        return services;
    }

    /// <summary>
    ///     Registers the CQRSharp exception-to-ProblemDetails mapping and configures its
    ///     <see cref="CqrsProblemDetailsOptions" />.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Overrides status codes or disables individual mappings.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services" /> or <paramref name="configure" /> is <see langword="null" />.</exception>
    /// <seealso cref="AddCqrsProblemDetails(IServiceCollection)" />
    public static IServiceCollection AddCqrsProblemDetails(this IServiceCollection services,
        Action<CqrsProblemDetailsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.AddCqrsProblemDetails();
        services.Configure(configure);
        return services;
    }
}
