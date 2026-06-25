using CQRSharp.Abstractions.Interfaces.Transactions;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines.Behaviors.Exceptions;
using CQRSharp.Pipelines.Behaviors.Idempotency;
using CQRSharp.Pipelines.Behaviors.Logging;
using CQRSharp.Pipelines.Behaviors.RateLimiting;
using CQRSharp.Pipelines.Behaviors.Resilience;
using CQRSharp.Pipelines.Behaviors.Timeout;
using CQRSharp.Pipelines.Behaviors.Transactions;
using CQRSharp.Pipelines.Behaviors.Validation;
using CQRSharp.Pipelines.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CQRSharp.Pipelines.Extensions;

public static class DependencyInjectionExtensions
{
    /// <summary>
    ///     Registers request-level exception hook support.
    /// </summary>
    public static IServiceCollection AddExceptionHandling(this IServiceCollection services)
    {
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ExceptionHandlingBehavior<,>));
        services.AddTransient(typeof(IStreamPipelineBehavior<,>), typeof(StreamExceptionHandlingBehavior<,>));
        return services;
    }

    /// <summary>
    ///     Registers the idempotency behavior, which enforces at-most-once processing for requests implementing
    ///     <c>IIdempotentRequest</c>. A duplicate request is rejected with a <c>DuplicateRequestException</c>.
    /// </summary>
    /// <remarks>
    ///     The caller must also register an
    ///     <see cref="CQRSharp.Abstractions.Interfaces.Idempotency.IIdempotencyStore" /> implementation.
    /// </remarks>
    public static IServiceCollection AddIdempotency(this IServiceCollection services)
    {
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
        return services;
    }

    /// <summary>
    ///     Registers the logging behavior, which logs the start, completion (with elapsed time), and failure of each
    ///     request and streaming request. Opt-in (off by default); runs outermost.
    /// </summary>
    public static IServiceCollection AddLoggingBehavior(this IServiceCollection services)
    {
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddTransient(typeof(IStreamPipelineBehavior<,>), typeof(StreamLoggingBehavior<,>));
        return services;
    }

    /// <summary>
    ///     Registers the resilience pipeline behavior in the service collection.
    /// </summary>
    public static IServiceCollection AddResilienceBehavior(
        this IServiceCollection services,
        Action<ResilienceOptions> configureOptions)
    {
        if (configureOptions == null)
            throw new ArgumentNullException(nameof(configureOptions), "Resilience configuration must be provided.");

        services.AddOptions<ResilienceOptions>()
            .Configure(configureOptions.Invoke)
            .Validate(o => o.MaxRetries >= 0, "ResilienceOptions.MaxRetries must be non-negative.")
            .Validate(o => o.BaseDelay >= TimeSpan.Zero, "ResilienceOptions.BaseDelay must be non-negative.")
            .ValidateOnStart();

        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ResilienceBehavior<,>));
        services.AddTransient(typeof(IStreamPipelineBehavior<,>), typeof(StreamResilienceBehavior<,>));

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

        services.AddOptions<TimeoutOptions>()
            .Configure(configureOptions.Invoke)
            .Validate(o => o.Timeout > TimeSpan.Zero, "TimeoutOptions.Timeout must be greater than zero.")
            .ValidateOnStart();

        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(TimeoutBehavior<,>));
        services.AddTransient(typeof(IStreamPipelineBehavior<,>), typeof(StreamTimeoutBehavior<,>));

        return services;
    }

    /// <summary>
    ///     Registers the validation pipeline behavior in the service collection.
    ///     This behavior executes all registered <c>IRequestValidator&lt;TRequest&gt;</c> implementations for a request.
    /// </summary>
    public static IServiceCollection AddValidationBehavior(this IServiceCollection services)
    {
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddTransient(typeof(IStreamPipelineBehavior<,>), typeof(StreamValidationBehavior<,>));
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
            configureOptions.Invoke(options);

            if (options.MaxTokens <= 0 || options.ReplenishRatePerSecond <= 0 || options.MaxEntries <= 0)
                throw new ArgumentException(
                    $"Rate limiting configuration is invalid. {nameof(options.MaxTokens)}, {nameof(options.ReplenishRatePerSecond)}, and {nameof(options.MaxEntries)} must be greater than zero.");
        });

        services.AddSingleton<RateLimiter>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RateLimitingBehavior<,>));
        services.AddTransient(typeof(IStreamPipelineBehavior<,>), typeof(StreamRateLimitingBehavior<,>));

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
        services.AddTransient(typeof(IStreamPipelineBehavior<,>), typeof(StreamUnitOfWorkBehavior<,>));

        return services;
    }

    public static IServiceCollection AddUnitOfWorkBehavior(
        this IServiceCollection services,
        Func<IServiceProvider, IUnitOfWork> implementationFactory,
        Action<UnitOfWorkOptions>? configureOptions = null)
    {
        services.Configure<UnitOfWorkOptions>(opts => { configureOptions?.Invoke(opts); });
        services.AddScoped<IUnitOfWork>(implementationFactory);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(UnitOfWorkBehavior<,>));
        services.AddTransient(typeof(IStreamPipelineBehavior<,>), typeof(StreamUnitOfWorkBehavior<,>));
        return services;
    }

    public static IServiceCollection AddCqrsPipelinePack(
        this IServiceCollection services,
        Action<CqrsPipelinePackOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Ensure the clock seam is available even if the pipeline pack is wired without the core AddCqrs call.
        services.TryAddSingleton(TimeProvider.System);

        var pack = new CqrsPipelinePackOptions();
        configure?.Invoke(pack);

        if (pack.IncludeExceptionHandling)
            services.AddExceptionHandling();

        if (pack.IncludeValidation)
            services.AddValidationBehavior();

        if (pack.IncludeLogging)
            services.AddLoggingBehavior();

        if (pack.ConfigureRateLimiting is not null)
            services.AddRateLimiting(pack.ConfigureRateLimiting);

        if (pack.UnitOfWorkFactory is not null)
            services.AddUnitOfWorkBehavior(pack.UnitOfWorkFactory, pack.ConfigureUnitOfWork);

        if (pack.ConfigureTimeout is not null)
            services.AddTimeoutBehavior(pack.ConfigureTimeout);

        if (pack.ConfigureResilience is not null)
            services.AddResilienceBehavior(pack.ConfigureResilience);

        return services;
    }
}