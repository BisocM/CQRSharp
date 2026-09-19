namespace CQRSharp.Core.Pipelines;

public sealed partial class PipelineExecutor
{
    // The interceptors arrive pre-sorted from the request plan; pre-handlers are short enough to run inline at their
    // call sites, so only the post-handler fan-out lives here.
    private static async Task InvokePostHandlers(IPostHandlerAttribute[] postHandlers, IRequest request, RequestOutcome outcome, IServiceProvider sp, CancellationToken ct)
    {
        // Every post-handler receives the request's outcome (the returned value or the thrown exception) and runs on
        // both the success and the exception paths.
        for (var i = 0; i < postHandlers.Length; i++)
            await postHandlers[i].OnAfterHandle(request, outcome, sp, ct).ConfigureAwait(false);
    }

    // The exception-path variant: outcome-aware post-handlers also observe a thrown failure, but an audit/post-handler
    // error must never mask the original exception that is about to propagate.
    private static async Task InvokePostHandlersIsolated(IPostHandlerAttribute[] postHandlers, IRequest request, RequestOutcome outcome, IServiceProvider sp, CancellationToken ct)
    {
        try
        {
            await InvokePostHandlers(postHandlers, request, outcome, sp, ct).ConfigureAwait(false);
        }
        catch
        {
            // Swallow: the original failure must surface, not a post-handler's.
        }
    }
}
