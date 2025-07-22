using CQRSharp.Core.Extensions;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Pipelines.Extensions;
using CQRSharp.Pipelines.Types.RateLimiting;
using CQRSharp.Sample.Context;
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
    //Please beware that the current sample ONLY functions with non-AoT compilation, due to the presence of MenuManager.
    //This sample is in place to showcase basic startup & usage, and does not display Native AoT compatibility.
    public static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices((_, services) =>
            {
                //Add CQRSharp to the services, scanning the current assembly for handlers and attributes
                services.AddCqrs(options =>
                    {
                        //Synchronous run mode so we can observe results directly
                        options.RunMode = RunMode.Sync;
                    })
                    .AddGenerated()
                    .AddRateLimiting(options =>
                    {
                        options.MaxTokens = 5;
                        options.ReplenishRatePerSecond = 1;
                        options.Scope = RateLimitScope.Global;
                    })
                    .AddTimeoutBehavior(o => { o.Timeout = TimeSpan.FromMilliseconds(10000); })
                    .AddResilienceBehavior(o => { o.MaxRetries = 1; })
                    .AddTransient<IRequestContextFactory<SampleRequestContext>,
                        CustomRequestContextFactory>(); //Register our context factory here! AFTER CQRSharp is configured.

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