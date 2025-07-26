using CQRSharp.Abstractions.Data.Interfaces.Outbox;
using CQRSharp.Abstractions.Data.Interfaces.Transactions;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines.Options;
using CQRSharp.Pipelines.Types.RateLimiting;
using CQRSharp.Pipelines.Types.Resilience;
using CQRSharp.Pipelines.Types.Timeout;
using CQRSharp.Pipelines.Types.Transactions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Pipelines.Extensions;

public static class DependencyInjectionExtensions
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

        services.Configure<ResilienceOptions>(configureOptions.Invoke);

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

        services.Configure<TimeoutOptions>(configureOptions.Invoke);

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
            configureOptions.Invoke(options);
        });

        services.AddSingleton<RateLimiter>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RateLimitingBehavior<,>));

        return services;
    }

    /// <summary>
    ///     Adds the Unit of Work behavior to the CQRS pipeline in an AoT-compatible way.
    /// </summary>
    /// <typeparam name="TUnitOfWork">Your concrete implementation of IUnitOfWork (e.g., an EF Core specific UoW).</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="implementationFactory">An AoT-safe factory delegate to create an instance of your TUnitOfWork.</param>
    /// <param name="configureOptions">Options for UoW configuration.</param>
    /// <returns>The service collection to allow chaining.</returns>
    /// <example>
    ///     <code>
    /// services.AddUnitOfWorkBehavior&lt;MyEfCoreUnitOfWork&gt;(sp => 
    ///     new MyEfCoreUnitOfWork(sp.GetRequiredService&lt;MyDbContext&gt;()));
    /// </code>
    /// </example>
    public static IServiceCollection AddUnitOfWorkBehavior<TUnitOfWork>(
        this IServiceCollection services,
        Func<IServiceProvider, TUnitOfWork> implementationFactory,
        Action<UnitOfWorkOptions>? configureOptions = null)
        where TUnitOfWork : class, IUnitOfWork
    {
        // Add configuration for UnitOfWorkOptions
        services.Configure<UnitOfWorkOptions>(opts => { configureOptions?.Invoke(opts); });

        // Register the concrete UoW using a factory delegate. This is AoT-safe
        // as it gives the compiler a static reference to the constructor.
        services.AddScoped<IUnitOfWork>(implementationFactory);
        
        // Register the pipeline behavior.
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(UnitOfWorkBehavior<,>));

        return services;
    }
}