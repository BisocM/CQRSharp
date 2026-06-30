using CQRSharp.Core.Options.Enums;

namespace CQRSharp.Core.Options;

/// <summary>
///     Represents the configuration options for the dispatcher.
/// </summary>
public sealed class DispatcherOptions
{
    /// <summary>
    ///     Controls where a dispatched command or query executes. <see cref="Enums.RunMode.Inline" /> (the default) runs
    ///     the handler inline on the caller's asynchronous flow; <see cref="Enums.RunMode.Queued" /> funnels the dispatch
    ///     through the shared background task queue for centralized throttling and back-pressure. Both modes return the
    ///     handler's result to the caller — the difference is scheduling, not fire-and-forget.
    /// </summary>
    /// <remarks>
    ///     The default value is <see cref="Enums.RunMode.Inline" />.
    /// </remarks>
    public RunMode RunMode { get; set; } = RunMode.Inline;

    /// <summary>
    ///     Controls whether request execution runs within the current DI scope (default)
    ///     or in a newly-created child scope.
    /// </summary>
    /// <remarks>
    ///     The default value is <see cref="ExecutionScopeMode.Current" /> to match MediatR-style scope semantics,
    ///     allowing nested sends/publishes to share scoped services (DbContext/UnitOfWork/etc.).
    /// </remarks>
    public ExecutionScopeMode ScopeMode { get; set; } = ExecutionScopeMode.Current;
}