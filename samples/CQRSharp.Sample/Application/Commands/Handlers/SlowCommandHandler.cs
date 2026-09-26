using CQRSharp.Sample.Application.Commands.Requests;

namespace CQRSharp.Sample.Application.Commands.Handlers;

public sealed class SlowCommandHandler : ICommandHandler<SlowCommand>
{
    // Longer than the timeout Program.cs configures; the timeout behavior cancels the token and the delay ends early.
    public async Task<CommandResult> Handle(SlowCommand command, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        return CommandResult.FromSuccess();
    }
}
