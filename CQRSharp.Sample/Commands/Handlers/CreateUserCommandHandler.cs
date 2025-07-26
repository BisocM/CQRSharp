using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Core.Notifications;
using CQRSharp.Sample.Commands.Types;
using CQRSharp.Sample.Data;
using CQRSharp.Sample.Notifications;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Commands.Handlers;

public class CreateUserCommandHandler(
    CustomInMemoryUserStore userStore,
    INotificationDispatcher dispatcher,
    ILogger<CreateUserCommandHandler> logger)
    : ICommandHandler<CreateUserCommand>
{
    public async Task<CommandResult> Handle(CreateUserCommand command, CancellationToken cancellationToken)
    {
        logger.LogInformation("Handling CreateUserCommand for user: {Name}", command.Name);

        var user = new User(command.Name, command.Id);
        userStore.AddUser(user);
        logger.LogInformation("User added to in-memory store.");

        var notification = new UserCreatedNotification(command.Id, command.Name);
        await dispatcher.Publish(notification, cancellationToken);
        logger.LogInformation("Published UserCreatedNotification. It should be captured by the outbox.");

        return CommandResult.FromSuccess();
    }
}