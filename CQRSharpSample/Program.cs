using CQRSharp.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using CQRSharp.Core.Dispatch;
using CQRSharpSample.Commands;
using CQRSharp.Core.Pipeline.Types;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Core.Events;

namespace CQRSharpSample
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            //Set up the DI container
            var services = new ServiceCollection();
            services.AddCQRS(options =>
            {
                options.EnableExecutionContextLogging = false;
                options.EnableSensitiveDataLogging = true;

                options.Timeout = TimeSpan.FromSeconds(1);

                options.RunMode = RunMode.Async;
            }, Assembly.GetExecutingAssembly());
            
            //Add the pipelines you may want
            services.AddPipelines(
                typeof(ExecutionLoggingBehavior<,>),
                typeof(ResilienceBehavior<,>),
                typeof(TimeoutBehavior<,>));

            //Build service provider
            var serviceProvider = services.BuildServiceProvider();

            await ExampleCommandCall(serviceProvider);
        }

        public static async Task ExampleCommandCall(ServiceProvider serviceProvider)
        {
            //Get the dispatcher
            var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

            #region Event Test

            var eventManager = serviceProvider.GetRequiredService<EventManager>();

            eventManager.QueryCompleted += (sender, e, ct) =>
            {
                Console.WriteLine($"Query completed: {e.Result}");
            };

            #endregion

            #region Command Example

            //Create a cancellation token (optional)
            var cancellationToken = new CancellationToken();

            //Send CreateUserCommand
            var createUserCommand = new CreateUserCommand
            {
                Username = "john_doe",
                Email = "john@example.com"
            };

            //Send the command to create the user.
            await dispatcher.ExecuteCommand(createUserCommand, cancellationToken);

            #endregion

            #region Query Example

            //Send CalculateSumQuery
            var calculateSumCommand = new CalculateSumQuery
            {
                Value1 = 10,
                Value2 = 20
            };

            var sumResult = await dispatcher.ExecuteQuery<int>(calculateSumCommand, cancellationToken);

            Console.WriteLine($"The sum is: {sumResult}");

            #endregion

            //Wait for the user creation to complete, since it was offloaded to a background thread.
            await Task.Delay(50000);
        }
    }
}