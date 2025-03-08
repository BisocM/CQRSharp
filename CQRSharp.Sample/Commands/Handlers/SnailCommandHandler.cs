using System;
using System.Threading;
using System.Threading.Tasks;
using CQRSharp.Data.Commands;
using CQRSharp.Interfaces.Handlers;
using CQRSharp.Sample.Commands.Types;
using CQRSharp.Sample.Management.Menu;

namespace CQRSharp.Sample.Commands.Handlers;

public class SnailCommandHandler : ICommandHandler<SnailCommand>
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
        return CommandResult.FromSuccess();
    }
}