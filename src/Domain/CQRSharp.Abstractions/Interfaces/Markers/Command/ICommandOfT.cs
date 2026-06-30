using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Models.Commands;

namespace CQRSharp.Abstractions.Interfaces.Markers.Command;

/// <summary>
///     A command that mutates state <em>and</em> returns a value, dispatched with <c>ICqrsDispatcher.Send</c> and
///     handled by <c>IResultCommandHandler&lt;TCommand, TResult&gt;</c>. The handler returns a
///     <see cref="CommandResult{TResult}" /> — outcome plus the value on success.
/// </summary>
/// <remarks>
///     Use this ONLY for a value no query could ever return: a server-side secret minted at the instant of the
///     operation and never persisted in readable form (a one-time API key, a TOTP seed, a token shown once). For any
///     value a query can reproduce, return a plain <see cref="CommandResult" /> and read it with an
///     <c>IQuery&lt;TResult&gt;</c> — the analyzer <c>CQRA009</c> flags this choice as a reminder.
/// </remarks>
/// <typeparam name="TResult">The value produced on success, wrapped in <see cref="CommandResult{TResult}" />.</typeparam>
public interface ICommand<TResult> : IRequest<CommandResult<TResult>>, ICommandMarker;
