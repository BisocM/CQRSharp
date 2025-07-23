using System.Data;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Query;

namespace CQRSharp.Pipelines.Types.Transactions.Interfaces;

/// <summary>
/// A non-generic marker interface that identifies a query as requiring a transaction.
/// This is used by the pipeline to apply transactional behavior without needing to know the query's result type.
/// </summary>
public interface ITransactionalQuery
{
    /// <summary>
    /// Gets the desired isolation level for the transaction.
    /// </summary>
    IsolationLevel IsolationLevel { get; set; }
    
    /// <summary>
    /// Property to signal a read-only intent
    /// </summary>
    bool IsReadOnly => true; 
}

/// <summary>
/// Marks a query as requiring a transaction, typically for read consistency.
/// The transaction will be automatically rolled back as no changes are saved.
/// </summary>
/// <typeparam name="TResult">The covariant result type of the query.</typeparam>
public interface ITransactionalQuery<out TResult> : ITransactionalQuery, IQuery<TResult>;