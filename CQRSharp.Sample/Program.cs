using System.Reflection;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Core.Pipelines.Types.RateLimiting;
using CQRSharp.Sample.Context;
using CQRSharp.Sample.Factories;
using CQRSharp.Sample.Models;
using CQRSharp.Sample.Notifications;
using CQRSharp.Sample.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample;

public class Program
{
    public static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                // Register user and request identification factories
                services.AddUserIdentificationFactory<SampleUserIdentificationFactory>();
                services.AddRequestIdentificationFactory<SampleRequestIdentificationFactory>();

                // Register rate limiting
                services.AddRateLimiting<SampleUserIdentificationFactory>(options =>
                {
                    options.MaxTokens = 5;
                    options.ReplenishRatePerSecond = 1;
                    options.Scope = RateLimitScope.Global;
                });

                // Register a simple in-memory repository
                services.AddSingleton<InMemoryUserRepository>();

                //Register custom context factory.
                services.AddTransient<IRequestContextFactory, CustomRequestContextFactory>();
                
                // Add CQRSharp to the services, scanning the current assembly for handlers and attributes
                services.AddCqrs(options =>
                {
                    // Enable execution context logging
                    options.EnableExecutionContextLogging = true;

                    // Disable sensitive data logging to show the redaction
                    options.EnableSensitiveDataLogging = false;

                    // Synchronous run mode so we can observe results directly
                    options.RunMode = RunMode.Sync;

                    options.Timeout = TimeSpan.FromSeconds(10);
                    options.MaxRetries = 3;
                }, Assembly.GetExecutingAssembly());

                // Register notification handlers
                services.AddTransient<UserCommandInitiatedHandler>();
                services.AddTransient<UserCommandCompletedHandler>();
                services.AddTransient<UserQueryInitiatedHandler>();
                services.AddTransient<UserQueryCompletedHandler>();

                // Add our demo hosted service
                services.AddHostedService<DemoHostedService>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .Build();

        await host.RunAsync();
    }
}