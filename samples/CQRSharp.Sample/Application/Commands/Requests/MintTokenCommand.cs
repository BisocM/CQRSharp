using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Models.Commands;

namespace CQRSharp.Sample.Application.Commands.Requests;

/// <summary>
///     A value-returning command (<see cref="ICommand{TResult}" />): mints a one-time token returned to the caller and
///     never stored in readable form. Exercises the source-generated value-command dispatch under Native AOT.
/// </summary>
public sealed class MintTokenCommand : ResultCommandBase<string>
{
    public required string Subject { get; init; }
}

/// <summary>Handler for <see cref="MintTokenCommand" />.</summary>
public sealed class MintTokenCommandHandler : IResultCommandHandler<MintTokenCommand, string>
{
    public Task<CommandResult<string>> Handle(MintTokenCommand command, CancellationToken cancellationToken)
        => Task.FromResult(string.IsNullOrEmpty(command.Subject)
            ? CommandResult<string>.FromError("subject required", 400)
            : CommandResult<string>.FromSuccess($"token:{command.Subject}"));
}
