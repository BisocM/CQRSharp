namespace CQRSharp;

/// <summary>
///     Opt-in marker for requests whose work must run at most once per <see cref="IdempotencyKey" />. The idempotency
///     behavior claims the key before the handler runs; a later request with the same key does not run the handler again.
/// </summary>
/// <remarks>
///     <para>
///         The key must be stable for a given logical request and unique across distinct requests (a client-supplied
///         request id, for example). Keys are not scoped by caller or request type: two requests that share a key are
///         duplicates of each other, so a key built from client input (an <c>Idempotency-Key</c> header) must include the
///         caller's identity, or one caller could receive another's replayed result. An empty key fails the request with
///         an <see cref="InvalidOperationException" />.
///     </para>
///     <para>What a duplicate gets back depends on the request that holds the key:</para>
///     <list type="bullet">
///         <item>
///             It completed: the duplicate is answered with the original result, and the handler does not run. A plain
///             <c>CommandResult</c> is always replayed; a value-carrying result (<c>CommandResult&lt;T&gt;</c>, a query
///             result) only when an <see cref="CQRSharp.Persistence.IIdempotencyResultSerializer" /> is registered and
///             could store it. A streaming request is never replayed.
///         </item>
///         <item>
///             It is still running, or its result cannot be replayed: the duplicate is rejected with a
///             <see cref="DuplicateRequestException" /> (<see cref="DuplicateRequestException.IsInProgress" /> tells the two
///             apart).
///         </item>
///         <item>
///             It carried a different payload fingerprint: the key was reused for another request, and the duplicate is
///             rejected with an <see cref="IdempotencyKeyMismatchException" />.
///         </item>
///     </list>
///     <para>
///         A request that throws or returns a failed <c>CommandResult</c> releases its key, so a retry runs again,
///         unless its unit of work commits the failed result (<c>UnitOfWorkOptions.RollbackOnFailedResult</c> set to
///         <c>false</c>); then the key completes and a duplicate gets the same failure back. The claims live in the
///         registered <see cref="CQRSharp.Persistence.IIdempotencyStore" />; enable the behavior with
///         <c>UseIdempotency(...)</c> on the CQRSharp builder.
///     </para>
/// </remarks>
public interface IIdempotentRequest : IRequest
{
    /// <summary>
    ///     A stable, unique key identifying this logical request for duplicate detection. Keep it within a bounded
    ///     length (at most 450 characters) so it stays portable across idempotency-store backends — relational stores
    ///     cap the key column, so an over-long key that works in-memory or in Redis can throw there; hash longer
    ///     natural keys down first. The key is matched with ordinal, case-sensitive equality.
    /// </summary>
    string IdempotencyKey { get; }
}