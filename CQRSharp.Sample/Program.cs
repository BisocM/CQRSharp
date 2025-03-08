using System;
using System.Threading.Tasks;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Core.Pipelines.Types.RateLimiting;
using CQRSharp.Sample.Data;
using CQRSharp.Sample.Factories;
using CQRSharp.Sample.Management.Cancellation;
using CQRSharp.Sample.Management.Menu;
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
                //Add CQRSharp to the services, scanning the current assembly for handlers and attributes
                services.AddCqrs(options =>
                {
                    //Synchronous run mode so we can observe results directly
                    options.RunMode = RunMode.Sync;

                })
                .AddRateLimiting(options =>
                {
                    options.MaxTokens = 5;
                    options.ReplenishRatePerSecond = 1;
                    options.Scope = RateLimitScope.Global;
                })
                .AddTimeoutBehavior(o =>
                {
                    o.Timeout = TimeSpan.FromSeconds(10);
                })
                .AddResilienceBehavior(o =>
                {
                    o.MaxRetries = 1;
                })
                .AddExecutionLoggingBehavior(o =>
                {
                    //Enable execution context logging
                    o.EnableExecutionContextLogging = true;

                    //Disable sensitive data logging to show the redaction
                    o.EnableSensitiveDataLogging = false;
                })
                .AddTransient<IRequestContextFactory, CustomRequestContextFactory>(); //Register our context factory here! AFTER CQRSharp is configured.
                
                //Register the MenuManager & the CancellationManager
                services.AddSingleton<MenuManager>();
                services.AddSingleton<CancellationManager>();
                
                //Add our demo hosted service
                services.AddHostedService<SampleHostedService>();

                services.AddSingleton<CustomInMemoryUserStore>();
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