using CQRSharp.Core.Notifications.Types;

namespace CQRSharp.Core.Options.Enums;

/// <summary>
///     Controls where a dispatched command or query actually executes: inline on the calling flow, or funneled through
///     the shared background task queue. In both modes the dispatch is awaitable and the handler's result flows back to
///     the caller — the difference is scheduling, not whether you get a result.
/// </summary>
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
    ///     back-pressure under load. Lifecycle notifications such as <see cref="CommandCompletedNotification" /> and
    ///     <see cref="QueryCompletedNotification{TResult}" /> are still published as normal. Not supported for streaming
    ///     requests.
    /// </summary>
    Queued
}
