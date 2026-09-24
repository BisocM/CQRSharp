using CQRSharp.Sample.Application.Commands.Requests;

namespace CQRSharp.Sample.Application.Commands.Handlers;

public sealed class PingCommandHandler : ICommandHandler<PingCommand>
{
    public Task<CommandResult> Handle(PingCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}
