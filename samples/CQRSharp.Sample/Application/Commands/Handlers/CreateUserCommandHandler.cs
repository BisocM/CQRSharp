using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Domain.Entities;
using CQRSharp.Sample.Domain.Events;
using CQRSharp.Sample.Infrastructure.Persistence;

namespace CQRSharp.Sample.Application.Commands.Handlers;

public sealed class CreateUserCommandHandler(CustomInMemoryUserStore userStore, ICqrsDispatcher dispatcher)
    : ICommandHandler<CreateUserCommand>
{
    public async Task<CommandResult> Handle(CreateUserCommand command, CancellationToken cancellationToken)
    {
        userStore.AddUser(new User(command.Name, command.Id));

        // Published while the command's transaction is open, so the outbox stores it with the user and the processor
        // delivers it once the transaction commits; a rolled-back command publishes nothing.
        await dispatcher.Publish(new UserCreatedNotification(command.Id, command.Name), cancellationToken);
        return CommandResult.FromSuccess();
    }
}
