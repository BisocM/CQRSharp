using CQRSharp.Data;
using CQRSharp.Interfaces.Handlers;
using CQRSharp.Sample.Commands;
using CQRSharp.Sample.Models;

namespace CQRSharp.Sample.Handlers
{
    public class CreateUserWithCustomContextCommandHandler(InMemoryUserRepository userRepo)
        : ICommandHandler<CreateUserWithCustomContextCommand>
    {
        public Task<CommandResult> Handle(CreateUserWithCustomContextCommand command, CancellationToken cancellationToken)
        {
            var newUser = new User
            {
                Id = Guid.NewGuid(),
                UserName = command.UserName,
                PasswordHash = command.Password
            };
            
            userRepo.AddUser(newUser);
            command.CreatedUserId = newUser.Id;
            return Task.FromResult(CommandResult.Success);
        }
    }
}