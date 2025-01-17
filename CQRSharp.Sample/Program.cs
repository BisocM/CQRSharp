using System.Reflection;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Core.Pipelines.Types.RateLimiting;
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
                    //Enable execution context logging
                    options.EnableExecutionContextLogging = true;

                    //Disable sensitive data logging to show the redaction
                    options.EnableSensitiveDataLogging = false;

                    //Synchronous run mode so we can observe results directly
                    options.RunMode = RunMode.Sync;

                    options.Timeout = TimeSpan.FromSeconds(10);
                    options.MaxRetries = 1;

                }, Assembly.GetExecutingAssembly())
                .AddRateLimiting(options =>
                {
                    options.MaxTokens = 5;
                    options.ReplenishRatePerSecond = 1;
                    options.Scope = RateLimitScope.Global;
                })
                .AddTransient<IRequestContextFactory, CustomRequestContextFactory>(); //Register our context factory here!
                
                //Register the MenuManager & the CancellationManager
                services.AddSingleton<MenuManager>();
                services.AddSingleton<CancellationManager>();
                
                //Add our demo hosted service
                services.AddHostedService<SampleHostedService>();
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