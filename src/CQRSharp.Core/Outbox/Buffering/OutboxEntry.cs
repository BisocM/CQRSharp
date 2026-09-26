namespace CQRSharp.Core.Outbox;

/// <summary>
///     A notification on its way to the outbox store, buffered or written straight through: what was published, and, for
///     a scheduled publish, when it is due.
/// </summary>
/// <param name="Notification">The published notification.</param>
/// <param name="DueAt">
///     The UTC time before which it must not be delivered, or <see langword="null" /> for an ordinary publish. Fixed when
///     it is published, so the time it waits in a buffer never delays it further.
/// </param>
internal readonly record struct OutboxEntry(INotification Notification, DateTime? DueAt = null);
