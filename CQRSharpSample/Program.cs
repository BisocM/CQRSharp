using CQRSharp.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Kafka.Extensions;
using CQRSharp.RateLimiting.Extensions;
using CQRSharpSample.Services;
using Microsoft.Extensions.Hosting;
using CQRSharp.RateLimiting.Enums;
using CQRSharp.RateLimiting.Options;

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

                    }, Assembly.GetExecutingAssembly());

                    services.AddRateLimiting<SimpleUserIdentifierFactory>(options =>
                    {
                        options.MaxTokens = 2;
                        options.ReplenishRatePerSecond = 20;
                        options.Scope = RateLimitScope.PerCommand;
                    });

                    services.AddHostedService<TestHostedService>();
                })
                .Build();

            await host.RunAsync();
        }
    }
}