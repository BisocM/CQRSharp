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
    ///     handlers do not run).
    /// </summary>
    Sequential,

    /// <summary>
    ///     Invoke all handlers concurrently and await them all, aggregating <b>every</b> failure into an
    ///     <see cref="System.AggregateException" />. This is the default.
    /// </summary>
    ParallelWhenAllAggregate
}
