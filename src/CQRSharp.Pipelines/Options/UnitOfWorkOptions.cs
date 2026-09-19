using System.Data;

namespace CQRSharp.Pipelines.Options;

/// <summary>
///     Provides configuration options for the Unit of Work behavior.
/// </summary>
public sealed class UnitOfWorkOptions
{
    /// <summary>
    ///     Gets or sets the default isolation level for transactions.
    ///     This is used if the request does not specify an isolation level.
    ///     Defaults to <see cref="IsolationLevel.ReadCommitted" />.
    /// </summary>
    public IsolationLevel DefaultIsolationLevel { get; set; } = IsolationLevel.ReadCommitted;

    /// <summary>
    ///     Whether a command that <em>returns</em> a failed <c>CommandResult</c> (<c>IsSuccess == false</c>) is treated
    ///     like one that threw: the transaction is rolled back (or, for an implicit unit of work, never saved) and the
    ///     notifications it published are discarded. Defaults to <c>true</c> — a command that reports failure should not
    ///     commit its writes or announce events for work it says did not happen. Set to <c>false</c> for handlers that
    ///     deliberately persist state before returning a failure (recording a failed login attempt, say).
    /// </summary>
    public bool RollbackOnFailedResult { get; set; } = true;
}