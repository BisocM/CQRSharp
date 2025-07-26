using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Sample.Commands.Types;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Commands.Handlers;

public class UpdateUserPasswordCommandHandler(ILogger<UpdateUserPasswordCommandHandler> logger) : ICommandHandler<UpdateUserPasswordCommand>
{
    public Task<CommandResult> Handle(UpdateUserPasswordCommand command, CancellationToken cancellationToken)
    {
        logger.LogInformation("Updating password for user {UserId}", command.UserId);
        return Task.FromResult(CommandResult.FromSuccess());
    }
}