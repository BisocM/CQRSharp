using CQRSharp.Core.Options;
using CQRSharp.Core.Pipelines;
using CQRSharp.Core.Pipelines.Types;
using CQRSharp.Core.Pipelines.Types.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Extensions;

public static partial class DependencyInjectionExtensions
{
    /// <summary>
    ///     Registers the resilience pipeline behavior in the service collection.
    /// </summary>
    public static IServiceCollection AddResilienceBehavior(
        this IServiceCollection services,
        Action<ResilienceOptions> configureOptions)
    {
        if (configureOptions == null)
            throw new ArgumentNullException(nameof(configureOptions), "Resilience configuration must be provided.");

        services.Configure<ResilienceOptions>(options =>
        {
            //Apply the delegate if it is provided.
            configureOptions?.Invoke(options);
        });

        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ResilienceBehavior<,>));

        return services;
    }

    /// <summary>
    ///     Registers the timeout pipeline behavior in the service collection.
    /// </summary>
    public static IServiceCollection AddTimeoutBehavior(
        this IServiceCollection services,
        Action<TimeoutOptions> configureOptions)
    {
        if (configureOptions == null)
            throw new ArgumentNullException(nameof(configureOptions), "Timeout configuration must be provided.");

        services.Configure<TimeoutOptions>(options =>
        {
            //Apply the delegate if it is provided.
            configureOptions?.Invoke(options);
        });

        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(TimeoutBehavior<,>));

        return services;
    }

    /// <summary>
    ///     Registers the rate limiting pipeline behavior in the dependency injection container.
    /// </summary>
    public static IServiceCollection AddRateLimiting(
        this IServiceCollection services,
        Action<RateLimiterOptions> configureOptions)
    {
        if (configureOptions == null)
            throw new ArgumentNullException(nameof(configureOptions), "Rate limiting configuration must be provided.");

        services.Configure<RateLimiterOptions>(options =>
        {
            if (options.MaxTokens <= 0 || options.ReplenishRatePerSecond <= 0)
                throw new ArgumentException(
                    $"Rate limiting configuration is invalid. {nameof(options.MaxTokens)} and {nameof(options.ReplenishRatePerSecond)} must be greater than zero.");

            //Apply the delegate if it is provided.
            configureOptions?.Invoke(options);
        });

        services.AddSingleton<RateLimiter>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RateLimitingBehavior<,>));

        return services;
    }
}