using System.Data;

namespace CQRSharp.Pipelines;

/// <summary>
///     Options for the unit-of-work behaviors (<c>UseUnitOfWork(...)</c>).
/// </summary>
public sealed class UnitOfWorkOptions
{
    /// <summary>
    ///     The isolation level a transaction begins with when its request does not name one (its <c>IsolationLevel</c>
    ///     is left at its default or set to <see cref="IsolationLevel.Unspecified" />). Defaults to
    ///     <see cref="IsolationLevel.Unspecified" />: the data store's own default. Must be <c>Unspecified</c> or a
    ///     defined level; host start fails otherwise.
    /// </summary>
    public IsolationLevel DefaultIsolationLevel { get; set; } = IsolationLevel.Unspecified;

    /// <summary>
    ///     Whether a command that <em>returns</em> a failed <c>CommandResult</c> (<c>IsSuccess == false</c>) is treated
    ///     like one that threw: the transaction is rolled back and the notifications it published are discarded.
    ///     Defaults to <c>true</c> — a command that reports failure should not commit its writes or announce events for
    ///     work it says did not happen. Set to <c>false</c> for handlers that deliberately persist state before returning
    ///     a failure (recording a failed login attempt, say): the failure is then committed with its notifications, and
    ///     an idempotent command keeps its idempotency key, so a duplicate gets the same failure back instead of running
    ///     the committed work again.
    /// </summary>
    public bool RollbackOnFailedResult { get; set; } = true;
}
