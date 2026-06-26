using CQRSharp.Abstractions.Models.Outbox;

namespace CQRSharp.EntityFrameworkCore.Persistence;

/// <summary>
///     The mutable EF Core row that persists a single outbox message. Mapping to and from the immutable public
///     <see cref="OutboxMessage" /> transport record lives in the mapper, so this type carries only persistence
///     concerns. It additionally tracks store-internal lease and concurrency state that has no place on the public
///     record: a visibility lease (<see cref="LockedUntil" />) and an optimistic-concurrency token
///     (<see cref="RowVersion" />) used to make the claim race-safe.
/// </summary>
public sealed class OutboxEntity
{
    /// <summary>The unique identifier of the message (primary key, supplied by the caller, never database-generated).</summary>
    public Guid Id { get; set; }

    /// <summary>The stable notification name used to resolve the handler at dispatch time.</summary>
    public string NotificationType { get; set; } = string.Empty;

    /// <summary>The serialized notification body.</summary>
    public byte[] Payload { get; set; } = [];

    /// <summary>The UTC timestamp at which the message was created; the FIFO ordering key for claims.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>The current processing status of the message.</summary>
    public OutboxMessageStatus Status { get; set; }

    /// <summary>The UTC timestamp at which the message was processed, or null if not yet processed.</summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>Error detail from the most recent failed delivery attempt, if any.</summary>
    public string? LastError { get; set; }

    /// <summary>The number of failed delivery attempts recorded so far; persisted so retry limits survive restarts.</summary>
    public int AttemptCount { get; set; }

    /// <summary>The earliest UTC time a failed message may be claimed again, or null to make it immediately eligible.</summary>
    public DateTime? NextRetryAt { get; set; }

    /// <summary>The W3C traceparent captured at enqueue time so the dispatch span can link to the originating trace.</summary>
    public string? TraceParent { get; set; }

    /// <summary>
    ///     Store-internal visibility lease: while a message is in progress this holds the UTC time until which the
    ///     claim is held. A message stuck in progress past this time is treated as abandoned (its claimant crashed)
    ///     and becomes claimable again. Kept off the public <see cref="OutboxMessage" /> record because it is an
    ///     implementation detail of this store's reclaim logic, not transport state.
    /// </summary>
    public DateTime? LockedUntil { get; set; }

    /// <summary>
    ///     Optimistic-concurrency token. Configured as a manual row-version that the store increments on every
    ///     mutation; if two processors load the same row and both try to save, the second save sees a stale token
    ///     and EF raises a concurrency exception, which is exactly how the atomic claim is enforced.
    /// </summary>
    public uint RowVersion { get; set; }
}
