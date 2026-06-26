namespace CQRSharp.Abstractions.Interfaces.Markers.Request;

/// <summary>
///     Opt-in marker for requests that must be processed at most once. The idempotency behavior uses
///     <see cref="IdempotencyKey" /> to detect and reject duplicate executions of the same logical request.
/// </summary>
/// <remarks>
///     The key must be stable for a given logical request and unique across distinct requests (e.g. a client-supplied
///     request id, or a deterministic hash of the meaningful inputs). A duplicate (a request whose key was already
///     claimed and not released) is rejected with a
///     <see cref="CQRSharp.Abstractions.Models.Idempotency.DuplicateRequestException" />. Idempotency is enforced via a
///     consumer-provided <see cref="CQRSharp.Abstractions.Interfaces.Idempotency.IIdempotencyStore" />, registered with
///     <c>AddIdempotency()</c>.
/// </remarks>
public interface IIdempotentRequest : IRequest
{
    /// <summary>
    ///     A stable, unique key identifying this logical request for duplicate detection. Keep it within a bounded
    ///     length (at most 512 characters) so it stays portable across idempotency-store backends — relational stores
    ///     cap the key column, so an over-long key that works in-memory or in Redis can throw there; hash longer
    ///     natural keys down first. The key is matched with ordinal, case-sensitive equality.
    /// </summary>
    string IdempotencyKey { get; }
}