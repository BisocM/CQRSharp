using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Core.Notifications;
using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Domain.Entities;
using CQRSharp.Sample.Domain.Events;
using CQRSharp.Sample.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Application.Commands.Handlers;

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