using System.Data;
using CQRSharp.Abstractions.Interfaces.Markers.Command;

namespace CQRSharp.Abstractions.Interfaces.Transactions;

/// <summary>
///     Marks a command as requiring a transaction. Any command implementing this interface is automatically wrapped in
///     a transaction by the UnitOfWorkBehavior. This is the command-side counterpart to <see cref="ITransactionalQuery" />.
/// </summary>
public interface ITransactionalCommand : ICommand
{
    /// <summary>
    ///     Gets the desired isolation level for the transaction.
    /// </summary>
    IsolationLevel IsolationLevel { get; set; }
}
