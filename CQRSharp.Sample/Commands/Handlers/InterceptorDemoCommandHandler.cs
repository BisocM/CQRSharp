using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Sample.Commands.Types;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Commands.Handlers;

public class InterceptorDemoCommandHandler(ILogger<InterceptorDemoCommandHandler> logger) : ICommandHandler<InterceptorDemoCommand>
{
    public Task<CommandResult> Handle(InterceptorDemoCommand command, CancellationToken cancellationToken)
    {
        logger.LogInformation(">>> Inside the InterceptorDemoCommandHandler. The pre-handler ran before this, and the post-handler will run after.");
        return Task.FromResult(CommandResult.FromSuccess());
    }
}