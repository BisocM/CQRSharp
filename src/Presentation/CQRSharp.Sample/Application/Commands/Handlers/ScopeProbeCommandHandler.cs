using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Commands.Handlers;

public sealed class ScopeProbeCommandHandler(SampleScopedMarker marker) : ICommandHandler<ScopeProbeCommand>
{
    public Task<CommandResult> Handle(ScopeProbeCommand command, CancellationToken cancellationToken)
    {
        command.HandlerScopeId = marker.Id;
        return Task.FromResult(CommandResult.FromSuccess());
    }
}

