using CQRSharp.Sample.Commands.Types;
using CQRSharp.Sample.Data;
using CQRSharp.Shared.Data.Interfaces.Handlers;
using CQRSharp.Shared.Data.Models.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Sample.Commands.Handlers;

public class DisplayUsersCommandHandler(IServiceProvider services) : ICommandHandler<DisplayUsersCommand>
{
    public async Task<CommandResult> Handle(DisplayUsersCommand command, CancellationToken cancellationToken)
    {
        //We can now handle the display of all users.
        Console.WriteLine("Displaying all users below:");

        //Get the all the users from the user store.
        var userStore = services.GetRequiredService<CustomInMemoryUserStore>();
        var userList = userStore.GetUsers();

        foreach (var user in userList)
            Console.WriteLine(user.Name);

        return CommandResult.FromSuccess();
    }
}