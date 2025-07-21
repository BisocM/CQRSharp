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
    ///     The executing call does not await for task completion, thus, will always return a default result.
    ///     In order to retrieve the results of asynchronous execution, you must subscribe to the
    ///     <see cref="CommandCompletedNotification" />
    ///     or the <see cref="QueryCompletedNotification{TResult}" />.
    /// </summary>
    /// <remarks>
    ///     In this mode, operations are executed asynchronously, meaning the initiating thread will not
    ///     wait for the operation's completion before proceeding to the next instruction. This allows for
    ///     non-blocking execution and can improve performance in applications where concurrency is beneficial.
    /// </remarks>
    Async
}