using CQRSharp.Sample.Application.Commands.Requests;

namespace CQRSharp.Sample.Application.Commands.Handlers;

public sealed class MintTokenCommandHandler : IResultCommandHandler<MintTokenCommand, string>
{
    public Task<CommandResult<string>> Handle(MintTokenCommand command, CancellationToken cancellationToken)
        => Task.FromResult(string.IsNullOrWhiteSpace(command.Subject)
            ? CommandResult<string>.Invalid(new ValidationFailure("SUBJECT_REQUIRED", "A subject is required.", nameof(command.Subject)))
            : CommandResult<string>.FromSuccess($"token:{command.Subject}"));
}
