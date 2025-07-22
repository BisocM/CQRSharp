using CQRSharp.Sample.Commands.Types;
using CQRSharp.Sample.Context;
using CQRSharp.Sample.Management.Menu;
using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Abstractions.Data.Models.Commands;

namespace CQRSharp.Sample.Commands.Handlers;

public class SnailCommandHandler : ICommandHandler<SnailCommand, SampleRequestContext>
{
    public async Task<CommandResult> Handle(SnailCommand command, CancellationToken cancellationToken)
    {
        Console.WriteLine(@"
                            o       o
                             \_____/ 
                             /=O=O=\     _______ 
                            /   ^   \   /\\\\\\\\
                            \ \___/ /  /\   ___  \
                             \_ V _/  /\   /\\\\  \
                               \  \__/\   /\ @_/  /
                                \____\____\______/
                            ");

        //Modify the state so that we return back to the primary menu.
        command.Context.NextState = MenuState.PrimaryMenu;
        Console.WriteLine($"User ID: {command.Context.UserId}");
        return CommandResult.FromSuccess();
    }
}