using CQRSharp.Sample.Application.Commands.Requests;
using CQRSharp.Sample.Infrastructure.SelfTest;

namespace CQRSharp.Sample.Application.Commands.Handlers;

public sealed class FailingCommandHandler(SampleDiagnostics diagnostics) : ICommandHandler<FailingCommand>
{
    public Task<CommandResult> Handle(FailingCommand command, CancellationToken cancellationToken)
    {
        // A retry sends the same request object again, so the attempt is counted per request, not in the handler, which
        // is a new instance each time.
        var attempt = diagnostics.CountRun(AttemptKey(command));
        if (attempt <= command.FailuresBeforeSuccess)
            throw new InvalidOperationException($"Simulated failure on attempt {attempt}.");

        return Task.FromResult(CommandResult.FromSuccess());
    }

    public static string AttemptKey(FailingCommand command) => $"failing-command:{command.Id:N}";
}
