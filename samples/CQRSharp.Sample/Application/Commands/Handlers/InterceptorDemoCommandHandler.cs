using CQRSharp.Sample.Application.Commands.Requests;

namespace CQRSharp.Sample.Application.Commands.Handlers;

public sealed class InterceptorDemoCommandHandler : ICommandHandler<InterceptorDemoCommand>
{
    public Task<CommandResult> Handle(InterceptorDemoCommand command, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.FromSuccess());
}
