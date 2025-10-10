using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Sample.Application.Commands.Requests;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Application.Commands.Handlers;

public class UpdateUserPasswordCommandHandler(ILogger<UpdateUserPasswordCommandHandler> logger) : ICommandHandler<UpdateUserPasswordCommand>
{
    public Task<CommandResult> Handle(UpdateUserPasswordCommand command, CancellationToken cancellationToken)
    {
        logger.LogInformation("Updating password for user {UserId}", command.UserId);
        return Task.FromResult(CommandResult.FromSuccess());
    }
}