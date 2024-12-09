using CQRSharp.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Core.Pipelines.Types.RateLimiting;
using CQRSharpSample.Services;
using Microsoft.Extensions.Hosting;

namespace CQRSharpSample
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddCqrs(options =>
                    {
                        options.EnableExecutionContextLogging = true;

                        options.RunMode = RunMode.Async;

                    }, Assembly.GetExecutingAssembly())
                    .AddRateLimiting<SimpleUserIdentifierFactory>(options =>
                    {
                        options.MaxTokens = 2;
                        options.ReplenishRatePerSecond = 20;
                        options.Scope = RateLimitScope.PerCommand;
                    })
                    .AddRequestIdentificationFactory<SimpleRequestIdentificationFactory>();
                    
                    //NOTE: There is no inherent conflict in calling AddRateLimiting() and AddUserIdentificationFactory() at the same time.
                    //it is just useless bloat, but AddRateLimiting() already registers the identification factory.
                    //.AddUserIdentificationFactory<SimpleUserIdentifierFactory>();

                    services.AddHostedService<TestHostedService>();
                })
                .Build();

            await host.RunAsync();
        }
    }
}