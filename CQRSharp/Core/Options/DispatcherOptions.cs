using CQRSharp.Core.Options.Enums;

namespace CQRSharp.Core.Options;

/// <summary>
///     Represents the configuration options for the dispatcher.
/// </summary>
public sealed class DispatcherOptions
{
    /// <summary>
    ///     The run mode for command execution.
    ///     If set to <see cref="Enums.RunMode.Async" />, commands will be executed asynchronously - meaning that concurrency
    ///     is allowed, and command execution is non-blocking.
    ///     If set to <see cref="Enums.RunMode.Sync" />, commands will be executed synchronously - meaning that commands will
    ///     be
    ///     executed in the order they are received.
    /// </summary>
    /// <remarks>
    ///     The default value is <see cref="Enums.RunMode.Sync" />.
    /// </remarks>
    public RunMode RunMode { get; set; } = RunMode.Sync;
}