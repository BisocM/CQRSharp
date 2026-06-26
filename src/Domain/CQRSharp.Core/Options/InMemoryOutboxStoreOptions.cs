namespace CQRSharp.Core.Options;

/// <summary>
///     Options for the in-process in-memory outbox store.
/// </summary>
public sealed class InMemoryOutboxStoreOptions
{
    /// <summary>
    ///     How long a claimed (in-progress) message stays leased before it becomes eligible to be claimed again.
    ///     This bounds how long a message remains stuck if the processor that claimed it crashes before finishing it,
    ///     so a later processor run can pick it back up. Must be greater than zero.
    /// </summary>
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
