using CQRSharp.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CQRSharp.Pipelines;

// The behavior registrations are internal: the fluent builder is the one public lane for enabling pipeline behaviors.
// Each registration is idempotent in itself (TryAdd for services and option validators, Configure for settings, which
// composes), so the builder can run them again for a second AddCqrsGenerated(builder) call on the same collection and
// every call's settings still apply.
internal static class DependencyInjectionExtensions
{
    /// <summary>Registers the request-level exception-hook behaviors.</summary>
    internal static IServiceCollection AddExceptionHandling(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(ExceptionHandlingBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IStreamPipelineBehavior<,>), typeof(StreamExceptionHandlingBehavior<,>)));
        return services;
    }

    /// <summary>
    ///     Registers the idempotency behaviors, which enforce at-most-once processing for requests implementing
    ///     <c>IIdempotentRequest</c>. The caller also registers an <see cref="IIdempotencyStore" />.
    /// </summary>
    internal static IServiceCollection AddIdempotency(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IStreamPipelineBehavior<,>), typeof(StreamIdempotencyBehavior<,>)));
        return services;
    }

    /// <summary>Registers the logging behaviors (<c>UseLogging()</c>).</summary>
    internal static IServiceCollection AddLoggingBehavior(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IStreamPipelineBehavior<,>), typeof(StreamLoggingBehavior<,>)));
        return services;
    }

    /// <summary>Registers the resilience behaviors and applies <paramref name="configureOptions" /> to their options.</summary>
    internal static IServiceCollection AddResilienceBehavior(this IServiceCollection services, Action<ResilienceOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.AddOptions<ResilienceOptions>().Configure(configureOptions).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ResilienceOptions>, ResilienceOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(ResilienceBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IStreamPipelineBehavior<,>), typeof(StreamResilienceBehavior<,>)));
        return services;
    }

    /// <summary>Registers the timeout behaviors and applies <paramref name="configureOptions" /> to their options.</summary>
    internal static IServiceCollection AddTimeoutBehavior(this IServiceCollection services, Action<TimeoutOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.AddOptions<TimeoutOptions>().Configure(configureOptions).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TimeoutOptions>, TimeoutOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(TimeoutBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IStreamPipelineBehavior<,>), typeof(StreamTimeoutBehavior<,>)));
        return services;
    }

    /// <summary>
    ///     Registers the validation behaviors, which run every registered <c>IRequestValidator&lt;TRequest&gt;</c> before
    ///     the handler.
    /// </summary>
    internal static IServiceCollection AddValidationBehavior(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IStreamPipelineBehavior<,>), typeof(StreamValidationBehavior<,>)));
        return services;
    }

    /// <summary>
    ///     Registers the rate-limiting behaviors and the shared <see cref="RequestRateLimiter" />, and applies
    ///     <paramref name="configureOptions" /> to the limiter's options.
    /// </summary>
    internal static IServiceCollection AddRateLimiting(this IServiceCollection services, Action<RateLimitingOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.AddOptions<RateLimitingOptions>().Configure(configureOptions).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RateLimitingOptions>, RateLimitingOptionsValidator>());
        services.TryAddSingleton<RequestRateLimiter>();
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(RateLimitingBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IStreamPipelineBehavior<,>), typeof(StreamRateLimitingBehavior<,>)));
        return services;
    }

    /// <summary>
    ///     Registers the unit-of-work behaviors with the factory for the application's <see cref="IUnitOfWork" />.
    /// </summary>
    /// <remarks>
    ///     Every reader resolves the unit of work singly, so the last registered factory is the one in effect: a host's
    ///     <c>UseUnitOfWork</c> overrides the one a library it references registered before it.
    /// </remarks>
    internal static IServiceCollection AddUnitOfWorkBehavior(
        this IServiceCollection services,
        Func<IServiceProvider, IUnitOfWork> implementationFactory,
        Action<UnitOfWorkOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(implementationFactory);

        var options = services.AddOptions<UnitOfWorkOptions>().ValidateOnStart();
        if (configureOptions is not null)
            options.Configure(configureOptions);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<UnitOfWorkOptions>, UnitOfWorkOptionsValidator>());

        services.AddScoped(implementationFactory);
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(UnitOfWorkBehavior<,>)));
        services.TryAddEnumerable(ServiceDescriptor.Transient(typeof(IStreamPipelineBehavior<,>), typeof(StreamUnitOfWorkBehavior<,>)));
        return services;
    }
}
