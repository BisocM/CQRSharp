using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Sample.Application.Commands.Requests;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Application.Commands.Handlers;

public class SlowCommandHandler(ILogger<SlowCommandHandler> logger) : ICommandHandler<SlowCommand>
{
    public async Task<CommandResult> Handle(SlowCommand command, CancellationToken cancellationToken)
    {
        logger.LogInformation("Handling SlowCommand. This will take 3 seconds...");
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        logger.LogInformation("SlowCommand finished. If you see this, the timeout didn't trigger.");
        return CommandResult.FromSuccess();
    }
}