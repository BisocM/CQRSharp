using CQRSharp.Data;
using CQRSharp.Interfaces.Handlers;
using CQRSharp.Sample.Commands;
using CQRSharp.Sample.Models;

namespace CQRSharp.Sample.Handlers;

public class UpdateUserPasswordCommandHandler : ICommandHandler<UpdateUserPasswordCommand>
{
    private readonly InMemoryUserRepository _userRepo;

    public UpdateUserPasswordCommandHandler(InMemoryUserRepository userRepo)
    {
        _userRepo = userRepo;
    }

    public Task<CommandResult> Handle(UpdateUserPasswordCommand command, CancellationToken cancellationToken)
    {
        var user = _userRepo.GetUser(command.UserId);
        if (user == null)
            return Task.FromResult(CommandResult.Fail);
        
        user.PasswordHash = command.NewPassword;
        return Task.FromResult(CommandResult.Success);
    }
}