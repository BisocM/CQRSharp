using CQRSharp.Core.Dispatch;
using CQRSharp.Core.Extensions;
using CQRSharp.Core.Pipelines.Attributes.Markers;
using CQRSharp.Core.Pipelines.Types;
using CQRSharp.Data.Commands;
using CQRSharp.Interfaces.Handlers;
using CQRSharp.Interfaces.Markers.Command;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CQRSharp.Generators.Sample
{
    // Sample command (implements your ICommand marker interface)
    [PipelineExemption(typeof(TimeoutBehavior<SampleCommand, CommandResult>))]
    [CustomInterceptor(2)]
    public class SampleCommand : CommandBase
    {
        [SensitiveData]
        public string Benny { get; set; }
    }
    
    // Sample command handler (implements ICommandHandler<T>)
    public class SampleCommandHandler : ICommandHandler<SampleCommand>
    {
        // The default parameterless constructor is now explicitly preserved.
        // (If you need dependencies, ensure they’re registered in DI.)
        public async Task<CommandResult> Handle(SampleCommand command, CancellationToken cancellationToken)
        {
            return CommandResult.FromSuccess();
        }
    }
    
    class Program
    {
        public static async Task Main(string[] args)
        {
            // Build a generic host with DI
            using IHost host = Host.CreateDefaultBuilder(args)
                .ConfigureServices((_, services) =>
                {
                    // Register CQRS components.
                    // The AddCqrs extension internally uses our generated static mapping for registration.
                    services.AddCqrs(options => { }, typeof(Program).Assembly)
                        .AddExecutionLoggingBehavior(o =>
                        {
                            
                        });
                })
                .Build();
            
            // Create a sample command and execute it.
            var command = new SampleCommand();
            
            var dispatcher = host.Services.GetRequiredService<IDispatcher>();
            var result = await dispatcher.ExecuteCommand(command);
            
            //Console.WriteLine($"Command result: {result}");
            Console.ReadLine();
        }
    }
}