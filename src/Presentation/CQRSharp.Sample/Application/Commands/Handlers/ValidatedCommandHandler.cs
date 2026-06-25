using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Commands.Handlers;

public sealed class ValidatedCommandHandler(SampleDiagnostics diagnostics) : ICommandHandler<ValidatedCommand>
{
    public Task<CommandResult> Handle(ValidatedCommand command, CancellationToken cancellationToken)
    {
        diagnostics.RecordValidatedCommandHandlerInvocation();
        return Task.FromResult(CommandResult.FromSuccess());
    }
}

