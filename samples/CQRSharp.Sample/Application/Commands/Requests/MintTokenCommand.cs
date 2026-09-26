namespace CQRSharp.Sample.Application.Commands.Requests;

/// <summary>
///     A value-returning command (<see cref="ICommand{TResult}" />): mints a one-time token returned to the caller and
///     never stored in readable form, so there is nothing a query could read it back from. It keeps the default request
///     context: nothing it does depends on who calls.
/// </summary>
public sealed class MintTokenCommand : ResultCommandBase<string>
{
    public required string Subject { get; init; }
}
