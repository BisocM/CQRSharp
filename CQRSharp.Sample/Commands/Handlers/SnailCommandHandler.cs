using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Sample.Commands.Types;
using CQRSharp.Sample.Context;
using CQRSharp.Sample.Management.Menu;
using CQRSharp.Sample.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Sample.Commands.Handlers;

public class SnailCommandHandler(IServiceProvider services) : ICommandHandler<SnailCommand, SampleRequestContext>
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