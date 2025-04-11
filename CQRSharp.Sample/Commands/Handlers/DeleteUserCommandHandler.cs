using CQRSharp.Sample.Commands.Types;
using CQRSharp.Sample.Data;
using CQRSharp.Shared.Data.Interfaces.Handlers;
using CQRSharp.Shared.Data.Models.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Sample.Commands.Handlers;

public class DeleteUserCommandHandler(IServiceProvider services) : ICommandHandler<DeleteUserCommand>
{
    public async Task<CommandResult> Handle(DeleteUserCommand command, CancellationToken cancellationToken)
    {
        //Display the users, then delete the user that was selected.
        //Get the all the users from the user store.
        var userStore = services.GetRequiredService<CustomInMemoryUserStore>();
        var userList = userStore.GetUsers();

        for (var index = 0; index < userList.Count; index++)
        {
            var user = userList[index];
            Console.WriteLine($"[{index + 1}] {user.Name} || {user.UserGuid}");
        }

        //Can't be bothered to add data type validation here. It's a demo after all.
        Console.WriteLine("Select the user that you want to delete.");
        int.TryParse(Console.ReadLine(), out var input);

        //Find the user via indexing
        var selectedUser = userList.ElementAt(input - 1);

        //Delete the user
        userStore.DeleteUser(selectedUser);

        return CommandResult.FromSuccess();
    }
}