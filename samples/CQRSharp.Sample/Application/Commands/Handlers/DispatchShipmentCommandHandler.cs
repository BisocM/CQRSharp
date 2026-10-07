using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Domain.Events;

namespace CQRSharp.Sample.Application.Commands.Handlers;

public sealed class DispatchShipmentCommandHandler(ICqrsDispatcher dispatcher) : ICommandHandler<DispatchShipmentCommand>
{
    public async Task<CommandResult> Handle(DispatchShipmentCommand command, CancellationToken cancellationToken)
    {
        // Published in the command's transaction: stored with its commit, then sent to RabbitMQ by the outbox processor
        // when the application forwards it, and delivered to the local handler all the same.
        await dispatcher.Publish(new ShipmentDispatchedNotification(command.ShipmentId, command.OrderId, command.Carrier), cancellationToken);
        return CommandResult.FromSuccess();
    }
}
