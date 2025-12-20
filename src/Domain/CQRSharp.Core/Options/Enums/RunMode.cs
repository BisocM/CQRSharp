using CQRSharp.Core.Notifications.Types;

namespace CQRSharp.Core.Options.Enums;

/// <summary>
///     Represents the mode in which operations are executed.
/// </summary>
/// <remarks>
///     This enumeration determines the behavior of command and query execution,
///     affecting whether execution is synchronous or asynchronous.
///     Asynchronous execution has a distinctly different way of handling results. Advise documentation.
/// </remarks>
public enum RunMode
{
    /// <summary>
    ///     Represents synchronous execution of operations.
    ///     The executing call will directly return the command results.
    /// </summary>
    /// <remarks>
    ///     In this mode, operations are executed synchronously, meaning the thread initiating the operation
    ///     will wait for its completion before proceeding to the next instruction. This is useful when
    ///     sequential processing is required and the order of execution must be preserved, on simple,
    ///     CLI-client-side applications.
    /// </remarks>
    Sync,

    /// <summary>
    ///     Represents asynchronous execution of operations.
    ///     The executing call schedules work onto the configured background task queue
    ///     and returns a task that completes when the queued operation finishes.
    ///     Notifications such as <see cref="CommandCompletedNotification" /> and <see cref="QueryCompletedNotification{TResult}" />
    ///     are still published as part of normal pipeline execution.
    /// </summary>
    /// <remarks>
    ///     In this mode, operations are executed asynchronously on the background queue, meaning the initiating
    ///     thread does not perform handler execution inline. This allows for centralized throttling/back-pressure
    ///     and consistent scheduling via the queue consumer.
    /// </remarks>
    Async
}
