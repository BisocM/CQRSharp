using CQRSharp.Data;
using CQRSharp.Data.Commands;
using CQRSharp.Interfaces.Handlers;
using CQRSharp.Sample.Commands;
using CQRSharp.Sample.Models;

namespace CQRSharp.Sample.Handlers;

public class CreateUserCommandHandler : ICommandHandler<CreateUserCommand>
{
    private readonly InMemoryUserRepository _userRepo;

    public CreateUserCommandHandler(InMemoryUserRepository userRepo)
    {
        _userRepo = userRepo;
    }

    public Task<CommandResult> Handle(CreateUserCommand command, CancellationToken cancellationToken)
    {
        var newUser = new User
        {
            Id = Guid.NewGuid(),
            UserName = command.UserName,
            PasswordHash = command.Password
        };

        _userRepo.AddUser(newUser);
        command.CreatedUserId = newUser.Id;
        return Task.FromResult(CommandResult.Success);
    }
}