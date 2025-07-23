using System.Data;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;

namespace CQRSharp.Pipelines.Types.Transactions.Interfaces;

/// <summary>
/// Marks a command as requiring a transaction. Any command implementing this interface
/// will be automatically wrapped in a transaction by the UnitOfWorkBehavior.
/// </summary>
public interface ITransactionalRequest : ICommand
{
    /// <summary>
    /// Gets the desired isolation level for the transaction.
    /// </summary>
    IsolationLevel IsolationLevel { get; set; }
}