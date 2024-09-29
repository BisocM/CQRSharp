using CQRSharp.Core.Pipeline.Attributes.Markers;
using CQRSharp.Core.Pipeline.Types;
using CQRSharp.Data;
using CQRSharp.Interfaces.Handlers;
using CQRSharp.Interfaces.Markers;

namespace CQRSharpSample.Commands
{
    //[LogEntry(1)]
    //[LogExit(1)]
    [PipelineExemption(typeof(TimeoutBehavior<,>))] //Demonstration of how to make a command exempt from behaviors.
    /// <summary>
    /// Command to create a new user.
    /// </summary>
    public class CreateUserCommand : ICommand
    {
        public required string Username { get; set; }

        [SensitiveData]
        public required string Email { get; set; }
    }

    /// <summary>
    /// Handler for creating a new user.
    /// </summary>
    public class CreateUserCommandHandler : ICommandHandler<CreateUserCommand>
    {
        public async Task<Unit> Handle(CreateUserCommand command, CancellationToken cancellationToken)
        {
            //To demonstrate the functionality of the timeout pipeline behavior. Change the number to trigger a timeout.
            await Task.Delay(2000, cancellationToken);

            //This can contain logic to create a user.
            //For example, have some kind of code that instantiates a user object
            //And later updates the database with that data.

            Console.WriteLine("User created successfully.");

            //Return Unit.Value to indicate completion.
            return Unit.Value;
        }
    }
}