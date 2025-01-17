using CQRSharp.Data.Commands;
using CQRSharp.Interfaces.Handlers;
using CQRSharp.Sample.Commands.Types;

namespace CQRSharp.Sample.Commands.Handlers;

public class DisplayUsersCommandHandler : ICommandHandler<DisplayUsersCommand>
{
    public async Task<CommandResult> Handle(DisplayUsersCommand command, CancellationToken cancellationToken)
    {
        //We can now handle the display of all users.
        Console.WriteLine("Displaying all users below:");
        
        return CommandResult.FromSuccess();
    }
}