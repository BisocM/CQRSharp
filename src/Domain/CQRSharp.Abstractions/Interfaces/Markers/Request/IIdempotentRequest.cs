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
    ///     A stable, unique key identifying this logical request for duplicate detection.
    /// </summary>
    string IdempotencyKey { get; }
}
