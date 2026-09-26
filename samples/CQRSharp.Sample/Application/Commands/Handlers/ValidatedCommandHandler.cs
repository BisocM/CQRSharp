using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Commands.Handlers;

public sealed class ValidatedCommandHandler(SampleDiagnostics diagnostics) : ICommandHandler<ValidatedCommand>
{
    public const string RunKey = "validated-command";

    public Task<CommandResult> Handle(ValidatedCommand command, CancellationToken cancellationToken)
    {
        diagnostics.CountRun(RunKey);
        return Task.FromResult(CommandResult.FromSuccess());
    }
}
