using CQRSharp.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Kafka.Extensions;
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
                    .AddKafka(kafkaOptions =>
                    {
                        //WIP Kafka configurations
                        kafkaOptions.CommandTopic = "customCommandTopic";
                    });
                    
                    services.AddHostedService<TestHostedService>();
                })
                .Build();

            await host.RunAsync();
        }
    }
}