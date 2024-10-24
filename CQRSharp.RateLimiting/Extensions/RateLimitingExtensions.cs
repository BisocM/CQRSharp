using CQRSharp.Core.Pipeline;
using CQRSharp.RateLimiting.Behaviors;
using CQRSharp.RateLimiting.Enums;
using CQRSharp.RateLimiting.Handlers;
using CQRSharp.RateLimiting.Options;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.RateLimiting.Extensions
{
    /// <summary>
    /// Provides extension methods for registering services related to the dispatcher.
    /// </summary>
    public static class RateLimitingExtensions
    {
        /// <summary>
        /// Adds the rate limiting behavior and all related services to the service collection.
        /// This method registers the <see cref="RateLimitingBehavior{TRequest,TResponse}"/> pipeline behavior, and a custom
        /// user identifier factory class provided by the library consumer.
        /// </summary>
        /// <typeparam name="TIdentifierService">
        /// The type that implements <see cref="IUserIdentifierFactory"/> used to retrieve unique user identifiers
        /// for rate limiting purposes. This must be implemented and provided by the library consumer.
        /// </typeparam>
        /// <param name="services">
        /// The <see cref="IServiceCollection"/> to which the rate limiting services will be added.
        /// </param>
        /// <param name="configureOptions">Configuration for the rate limiter.</param>
        /// <returns>
        /// The updated <see cref="IServiceCollection"/> instance, enabling chaining of registration calls.
        /// </returns>
        /// <remarks>
        /// This method adds essential components for rate limiting within the CQRS pipeline.
        /// </remarks>
        public static IServiceCollection AddRateLimiting<TIdentifierService>(
            this IServiceCollection services,
            Action<RateLimiterOptions> configureOptions) where TIdentifierService : class, IUserIdentifierFactory
        {
            //Validate that configuration action is provided
            if (configureOptions == null)
                throw new ArgumentNullException(nameof(configureOptions), "Rate limiting configuration must be provided.");

            //Create and configure RateLimiterConfig instance
            var config = new RateLimiterOptions()
            {
                MaxTokens = 0,
                ReplenishRatePerSecond = 0,
                Scope = RateLimitScope.Global
            };
            
            configureOptions(config);

            //Validate that the config has been populated
            if (config.MaxTokens <= 0 || config.ReplenishRatePerSecond <= 0)
                throw new ArgumentException("Rate limiting configuration is invalid. MaxTokens and ReplenishRatePerSecond must be greater than zero.");

            //Register the configured RateLimiterConfig as a singleton
            services.AddSingleton(config);

            //Register the rate limiter
            services.AddSingleton<RateLimiter>();

            //Register the rate limiting behavior
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(RateLimitingBehavior<,>));

            //Register the user identifier factory as a transient service
            services.AddTransient<IUserIdentifierFactory, TIdentifierService>();

            return services;
        }
    }
}