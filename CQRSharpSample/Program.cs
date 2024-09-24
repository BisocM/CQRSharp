using CQRSharp.Core.Extensions;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using CQRSharp.Core.Dispatch;
using CQRSharpSample.Commands;
using CQRSharp.Core.Pipeline.Types;

namespace CQRSharpSample
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            //Set up the DI container
            var services = new ServiceCollection();
            services.AddDispatcher(Assembly.GetExecutingAssembly());
            
            //Addthe pipelines you may want
            services.AddPipelines(typeof(LoggingBehavior<,>));

            //Build service provider
            var serviceProvider = services.BuildServiceProvider();

            await ExampleCommandCall(serviceProvider);
        }

        public static async Task ExampleCommandCall(ServiceProvider serviceProvider)
        {
            //Get the dispatcher
            var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

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
            await dispatcher.Send(createUserCommand, cancellationToken);

            #endregion

            #region Query Example

            //Send CalculateSumQuery
            var calculateSumCommand = new CalculateSumQuery
            {
                Value1 = 10,
                Value2 = 20
            };

            int sumResult = await dispatcher.Query(calculateSumCommand, cancellationToken);

            Console.WriteLine($"The sum is: {sumResult}");

            #endregion
        }
    }
}