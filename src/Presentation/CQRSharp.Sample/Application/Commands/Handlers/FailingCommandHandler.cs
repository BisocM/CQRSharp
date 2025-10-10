using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Sample.Application.Commands.Requests;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Application.Commands.Handlers;

public class FailingCommandHandler(ILogger<FailingCommandHandler> logger) : ICommandHandler<FailingCommand>
{
    private static int _attemptCount;

    public Task<CommandResult> Handle(FailingCommand command, CancellationToken cancellationToken)
    {
        _attemptCount++;
        logger.LogInformation("Handling FailingCommand. Attempt: {AttemptCount}", _attemptCount);

        if (_attemptCount <= 2)
        {
            logger.LogWarning("FailingCommand is failing on purpose.");
            throw new InvalidOperationException($"Simulated failure on attempt {_attemptCount}.");
        }

        logger.LogInformation("FailingCommand is now succeeding.");
        _attemptCount = 0; // Reset for next time
        return Task.FromResult(CommandResult.FromSuccess());
    }
}