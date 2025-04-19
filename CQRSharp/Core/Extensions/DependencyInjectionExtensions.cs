using CQRSharp.Core.BackgroundTasks;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using CQRSharp.Core.Requests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Core.Extensions
{
    /// <summary>
    /// Provides extension methods to register CQRSharp services
    /// and configurations into the dependency injection container.
    /// </summary>
    public static partial class DependencyInjectionExtensions
    {
        /// <summary>
        /// Registers the necessary CQRS services and configurations into the provided <c>IServiceCollection</c>.
        /// This includes dispatchers, handlers, factories, logging, background‑queue components, and defaults.
        /// </summary>
        /// <param name="services">The <c>IServiceCollection</c> used to register dependencies.</param>
        /// <param name="configureDispatcher">Optional: customize DispatcherOptions.</param>
        /// <param name="configureQueue">Optional: customize BackgroundTaskQueueOptions.</param>
        /// <returns>The updated <c>IServiceCollection</c> with all required CQRS services registered.</returns>
        public static IServiceCollection AddCqrs(this IServiceCollection services,
            Action<DispatcherOptions>? configureDispatcher = null,
            Action<BackgroundTaskQueueOptions>? configureQueue = null)
        {
            //Configure options
            services.Configure<DispatcherOptions>(opts =>
                configureDispatcher?.Invoke(opts));

            //Configure the background task queue
            services.Configure<BackgroundTaskQueueOptions>(opts =>
                configureQueue?.Invoke(opts));
            
            //Register core CQRS services
            services.AddSingleton<IDispatcher, Dispatcher>();
            services.AddSingleton<INotificationDispatcher, NotificationDispatcher>();
            services.AddTransient<IRequestContextFactory, DefaultRequestContextFactory>();
            
            //Background task queues
            services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
            services.AddHostedService<BackgroundTaskQueueConsumer>();

            //Ensure the host's logging pipeline is set up
            services.AddLogging(lb =>
            {
                lb.AddConsole();
                lb.SetMinimumLevel(LogLevel.Information);
            });

            //Register generated handlers *and* measure how many you added
            var before = services.Count;
            AddGeneratedHandlers(services);
            var handlerCount = services.Count - before;

            //Register the rest of the generated registries & factories
            AddGeneratedPipelineRegistry(services);
            AddGeneratedRequestRegistry(services);
            AddGeneratedHandlerRegistry(services);
            AddGeneratedFactories(services);

            //Persist that count so our hosted service can log it later
            services.AddSingleton(new CqrsRegistrationInfo(handlerCount));

            //Defer the actual "we registered X handlers" log until startup
            services.AddHostedService<CqrsStartupLogger>();

            return services;
        }
    }

    /// <summary>
    /// Holds how many handlers got registered.  Read by the hosted service at startup.
    /// </summary>
    internal sealed class CqrsRegistrationInfo(int handlerCount)
    {
        public int HandlerCount { get; } = handlerCount;
    }

    /// <summary>
    /// Runs once when the host starts, using the real ILogger.
    /// </summary>
    internal sealed class CqrsStartupLogger(
        ILogger<CqrsStartupLogger> logger,
        CqrsRegistrationInfo info)
        : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Successfully registered {HandlerCount} request handlers.",
                                   info.HandlerCount);
            logger.LogInformation("CQRS service registration completed.");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}