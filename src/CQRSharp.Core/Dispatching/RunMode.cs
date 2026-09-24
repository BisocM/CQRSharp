namespace CQRSharp;

/// <summary>
///     Controls where a sent command or query actually executes: inline on the calling flow, or funneled through the
///     shared background task queue. In both modes the dispatch is awaitable and the handler's result flows back to the
///     caller — the difference is scheduling, not whether you get a result.
/// </summary>
/// <remarks>
///     <para>
///         The run mode governs <c>Send</c> only. A stream (<c>Stream</c>) runs on the flow that enumerates it in either
///         mode, in the scope <see cref="DispatcherOptions.ScopeMode" /> gives it: its consumer pulls each item as it goes,
///         so there is no work a queue could run to completion on its own.
///     </para>
///     <para>
///         In either mode the request's context comes from its caller: the context factory is resolved from the scope of
///         the dispatcher the request is sent through and runs on the flow that sends it, before the request is queued.
///         A factory that reads the caller from a scoped service or from ambient state (the current HTTP user) therefore
///         sees the caller under <see cref="Queued" /> too, although the handler runs on the queue's consumer, in a scope
///         of its own.
///     </para>
/// </remarks>
public enum RunMode
{
    /// <summary>
    ///     Execute the handler inline on the caller's asynchronous flow (the default). The dispatch awaits the handler
    ///     directly; nothing is queued. Best for ordinary in-process request handling where the result is wanted with no
    ///     extra scheduling hop.
    /// </summary>
    Inline,

    /// <summary>
    ///     Funnel the dispatch through the configured background task queue instead of running it inline. The call still
    ///     returns an awaitable task that completes with the handler's result once the queued work runs — this is
    ///     <b>not</b> fire-and-forget — but execution is centrally throttled and scheduled by the queue consumer, giving
    ///     back-pressure under load. The handler and its behaviors run in a DI scope of their own, whatever
    ///     <see cref="DispatcherOptions.ScopeMode" /> says; a request sent from queued work runs at once, in a scope of its
    ///     own, instead of queueing behind the work that sent it. Lifecycle notifications such as
    ///     <see cref="CommandCompletedNotification" /> and <see cref="QueryCompletedNotification{TResult}" /> are still
    ///     published as normal. Streams are not queued: they run inline.
    /// </summary>
    Queued
}
