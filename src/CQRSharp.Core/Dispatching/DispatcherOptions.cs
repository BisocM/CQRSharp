namespace CQRSharp;

/// <summary>
///     Represents the configuration options for the dispatcher.
/// </summary>
/// <remarks>
///     Whatever the two modes say, a request's context is its caller's: the <see cref="IRequestContextFactory{TContext}" />
///     is resolved from the scope of the dispatcher the request is sent through and runs on the flow that sends it,
///     before the request is handed to the queue or to a scope of its own.
/// </remarks>
public sealed class DispatcherOptions
{
    /// <summary>
    ///     Controls where a sent command or query executes. <see cref="CQRSharp.RunMode.Inline" /> (the default) runs the
    ///     handler inline on the caller's asynchronous flow; <see cref="CQRSharp.RunMode.Queued" /> funnels the dispatch
    ///     through the shared background task queue for centralized throttling and back-pressure. Both modes return the
    ///     handler's result to the caller — the difference is scheduling, not fire-and-forget. Streams are not affected:
    ///     a stream always runs on the flow that enumerates it.
    /// </summary>
    /// <remarks>
    ///     The default value is <see cref="CQRSharp.RunMode.Inline" />.
    /// </remarks>
    public RunMode RunMode { get; set; } = RunMode.Inline;

    /// <summary>
    ///     Controls whether a sent request or an enumerated stream runs within the current DI scope (default) or in a
    ///     newly created scope of its own.
    /// </summary>
    /// <remarks>
    ///     The default value is <see cref="ExecutionScopeMode.Current" /> to match MediatR-style scope semantics,
    ///     allowing nested sends/publishes to share scoped services (DbContext/UnitOfWork/etc.). A queued command or query
    ///     runs in a scope of its own whatever this says; a stream follows it in every <see cref="RunMode" />.
    /// </remarks>
    public ExecutionScopeMode ScopeMode { get; set; } = ExecutionScopeMode.Current;
}
