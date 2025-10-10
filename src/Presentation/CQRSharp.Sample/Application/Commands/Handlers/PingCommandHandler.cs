using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Sample.Application.Commands.Requests;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Application.Commands.Handlers;

public class PingCommandHandler(ILogger<PingCommandHandler> logger) : ICommandHandler<PingCommand>
{
    public Task<CommandResult> Handle(PingCommand command, CancellationToken cancellationToken)
    {
        logger.LogInformation("Pong!");
        return Task.FromResult(CommandResult.FromSuccess());
    }
}