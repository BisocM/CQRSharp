using CQRSharp.Core.Dispatch;
using CQRSharpSample.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CQRSharpSample.Services
{
    internal class TestHostedService(IServiceProvider services) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            //Get the dispatcher
            var dispatcher = services.GetRequiredService<IDispatcher>();

            #region Command Example

            //Send CreateUserCommand
            var createUserCommand = new CreateUserCommand
            {
                Username = "john_doe",
                Email = "john@example.com"
            };

            //Send the command to create the user
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

            #endregion

            //Wait for the user creation to complete, since it was offloaded to a background thread
            await Task.Delay(5000, cancellationToken); // Reduced delay for demonstration

            Console.WriteLine("TestHostedService completed.");
        }
    }
}