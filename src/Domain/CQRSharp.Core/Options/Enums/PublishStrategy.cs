namespace CQRSharp.Core.Options.Enums;

/// <summary>
///     Controls how a notification is dispatched to its multiple handlers.
/// </summary>
public enum PublishStrategy
{
    /// <summary>
    ///     Invoke all handlers concurrently and await them all. The first failure is propagated; sibling failures are
    ///     observed but not surfaced.
    /// </summary>
    Parallel,

    /// <summary>
    ///     Invoke handlers one at a time, in order, awaiting each before the next. Stops at the first failure (later
    ///     handlers do not run). This is the default: handlers share the dispatching DI scope, so running them
    ///     sequentially avoids concurrent use of a shared non-thread-safe scoped service (e.g. an EF Core DbContext).
    /// </summary>
    Sequential,

    /// <summary>
    ///     Invoke all handlers concurrently and await them all, aggregating <b>every</b> failure into an
    ///     <see cref="System.AggregateException" />. Handlers run in parallel within the shared dispatching scope, so
    ///     only use this when the handlers do not share a non-thread-safe scoped service.
    /// </summary>
    ParallelWhenAllAggregate
}