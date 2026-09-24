using System.Data;

namespace CQRSharp;

/// <summary>
///     Marks a command as running in a unit-of-work transaction: the unit-of-work behavior begins one before the handler
///     runs and commits it when the command succeeds. Not tied to a result shape, so an outcome-only command
///     (<see cref="ICommand" />, <see cref="CommandBase" />) and a value-returning one (<see cref="ICommand{TResult}" />,
///     <see cref="ResultCommandBase{TResult}" />) opt in the same way. The query-side counterpart is
///     <see cref="ITransactionalQuery" />; a query that writes uses it with <see cref="ITransactionalQuery.IsReadOnly" />
///     <c>false</c>.
/// </summary>
/// <remarks>
///     The marker opts a command into a transaction; it does not make a request a command. What a request is comes from
///     its request interface alone, so a query or a stream that carries this marker is still dispatched as one (CQRA015
///     reports it).
/// </remarks>
public interface ITransactionalCommand
{
    /// <summary>
    ///     The isolation level for the transaction. Left at its default (or set to <see cref="IsolationLevel.Unspecified" />),
    ///     the unit of work's configured default applies, and without one the data store's own default.
    /// </summary>
    IsolationLevel IsolationLevel { get; }
}
