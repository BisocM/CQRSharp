using CQRSharp.Core.Options.Enums;

namespace CQRSharp.Core.Options;

/// <summary>
///     Configuration options for how notifications are dispatched to their handlers. Kept separate from
///     <see cref="DispatcherOptions" /> (which governs request execution) because notification fan-out is a distinct
///     concern with its own failure and concurrency semantics.
/// </summary>
public sealed class NotificationOptions
{
    /// <summary>
    ///     Controls how a notification is dispatched to its multiple handlers.
    /// </summary>
    /// <remarks>
    ///     The default is <see cref="PublishStrategy.Sequential" /> — handlers run one at a time. This is the safe
    ///     default because handlers for a notification share the dispatching DI scope, and a parallel strategy would
    ///     run them concurrently against any shared non-thread-safe scoped service (e.g. an EF Core <c>DbContext</c>).
    ///     Opt into <see cref="PublishStrategy.Parallel" /> / <see cref="PublishStrategy.ParallelWhenAllAggregate" />
    ///     only when the handlers are independent.
    /// </remarks>
    public PublishStrategy PublishStrategy { get; set; } = PublishStrategy.Sequential;
}
