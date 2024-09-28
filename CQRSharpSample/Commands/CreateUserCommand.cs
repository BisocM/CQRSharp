using CQRSharp.Core.Pipeline.Attributes.Markers;
using CQRSharp.Data;
using CQRSharp.Interfaces.Handlers;
using CQRSharp.Interfaces.Markers;
using CQRSharpSample.Attributes;

namespace CQRSharpSample.Commands
{
    [LogEntry]
    [LogExit]
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
        public Task<Unit> Handle(CreateUserCommand command, CancellationToken cancellationToken)
        {
            //This can contain logic to create a user.
            //For example, have some kind of code that instantiates a user object
            //And later updates the database with that data.

            //Logic here.

            //Return Unit.Value to indicate completion.
            return Task.FromResult(Unit.Value);
        }
    }
}