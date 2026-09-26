namespace CQRSharp;

/// <summary>
///     Options for the in-process in-memory outbox store and the inbox that pairs with it.
/// </summary>
public sealed class InMemoryOutboxStoreOptions
{
    /// <summary>
    ///     How long a claimed (in-progress) message stays leased before it becomes eligible to be claimed again.
    ///     This bounds how long a message remains stuck if the processor that claimed it crashes before finishing it,
    ///     so a later processor run can pick it back up. Must be greater than zero and at most 10 years.
    /// </summary>
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     How long a dead-lettered message is kept before the store drops it, measured from when it failed.
    ///     <see langword="null" /> (the default) keeps dead letters until they are requeued or purged: they are the record
    ///     of what could not be delivered. When set, must be greater than zero and at most 10 years.
    /// </summary>
    public TimeSpan? DeadLetterRetention { get; set; }

    /// <summary>
    ///     How long the inbox remembers a completed delivery, which is how long a redelivery of the same message is
    ///     recognised and skipped. Must comfortably exceed the visibility timeout, and be at most 10 years. Defaults to 7 days.
    /// </summary>
    public TimeSpan InboxRetention { get; set; } = TimeSpan.FromDays(7);
}
