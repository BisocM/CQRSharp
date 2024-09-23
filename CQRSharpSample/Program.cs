using CQRSharp.Core.Extensions;
using CQRSharp.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using CQRSharp.Core.Dispatch;
using CQRSharpSample.Commands;
using CQRSharpSample.Attributes;

namespace CQRSharpSample
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Set up the DI container
            var services = new ServiceCollection();
            services.AddDispatcher(Assembly.GetExecutingAssembly());

            // Build service provider
            var serviceProvider = services.BuildServiceProvider();

            await ExampleCommandCall(serviceProvider);
        }

        public static async Task ExampleCommandCall(ServiceProvider serviceProvider)
        {
            //Get the dispatcher
            var dispatcher = serviceProvider.GetRequiredService<IDispatcher>();

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

            //Send CalculateSumCommand
            var calculateSumCommand = new CalculateSumCommand
            {
                Value1 = 10,
                Value2 = 20
            };

            Console.WriteLine("Sending the sum command...");

            int sumResult = await dispatcher.Send(calculateSumCommand, cancellationToken);

            Console.WriteLine($"The sum is: {sumResult}");
        }
    }
}
