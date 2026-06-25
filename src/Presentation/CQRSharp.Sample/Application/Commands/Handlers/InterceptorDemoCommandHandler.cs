using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Sample.Application.Commands.Requests;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Sample.Application.Commands.Handlers;

public class InterceptorDemoCommandHandler(ILogger<InterceptorDemoCommandHandler> logger) : ICommandHandler<InterceptorDemoCommand>
{
    public Task<CommandResult> Handle(InterceptorDemoCommand command, CancellationToken cancellationToken)
    {
        logger.LogInformation(">>> Inside the InterceptorDemoCommandHandler. The pre-handler ran before this, and the post-handler will run after.");
        return Task.FromResult(CommandResult.FromSuccess());
    }
}