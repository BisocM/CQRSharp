namespace CQRSharp.Abstractions.Interfaces.Transactions;

/// <summary>
///     Provides information about the current transactional state of a request.
///     This is a scoped service used internally by the framework to determine
///     whether to dispatch notifications immediately or send them to an outbox.
/// </summary>
public interface ITransactionContext
{
    /// <summary>
    ///     Gets a value indicating whether the current request is executing within an active transaction.
    /// </summary>
    bool IsInTransaction { get; }
}