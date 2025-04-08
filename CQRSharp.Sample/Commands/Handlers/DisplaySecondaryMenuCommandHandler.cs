using CQRSharp.Sample.Commands.Types;
using CQRSharp.Sample.Management.Menu;
using CQRSharp.Shared.Core.Data.Interfaces.Handlers;
using CQRSharp.Shared.Core.Data.Models.Commands;

namespace CQRSharp.Sample.Commands.Handlers;

public class DisplaySecondaryMenuCommandHandler : ICommandHandler<DisplaySecondaryCommandMenu>
{
    public async Task<CommandResult> Handle(DisplaySecondaryCommandMenu command, CancellationToken cancellationToken)
    {
        //Nothing to be done here, we just need to set the context to display the secondary menu after the command is complete.
        //Keep in mind that this should not really be done at all. This is just to demonstrate the capabilities of the gerneric
        //context system of the library.
        //In an actual similar implementation, it is far better to simply use Queries with the SAME return type,
        //and pass the NextState in the return object. It is much cleaner and maintainable, and less abstracted.
        command.Context.NextState = MenuState.SecondaryMenu;
        return CommandResult.FromSuccess();
    }
}