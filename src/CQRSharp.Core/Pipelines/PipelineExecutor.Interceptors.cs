using CQRSharp.Pipelines;
using System.Collections.Concurrent;

namespace CQRSharp.Core.Pipelines;

public sealed partial class PipelineExecutor
{
    private static readonly ConcurrentDictionary<Type, IPreHandlerAttribute[]> SortedPreHandlerCache = new();
    private static readonly ConcurrentDictionary<Type, IPostHandlerAttribute[]> SortedPostHandlerCache = new();

    /// <summary>
    ///     Discovers and executes all <see cref="IPreHandlerAttribute" />s attached to the request class.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="sp">The scoped service provider.</param>
    /// <param name="ct">The cancellation token.</param>
    private static async Task InvokePreHandleAttributes(IRequest request, IServiceProvider sp, CancellationToken ct)
    {
        if (request.Metadata is null) return;

        var preHandlers = GetPreHandlers(request);
        for (var i = 0; i < preHandlers.Length; i++)
            await preHandlers[i].OnBeforeHandle(request, sp, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Discovers and executes all <see cref="IPostHandlerAttribute" />s attached to the request class.
    /// </summary>
    /// <param name="request">The request being processed.</param>
    /// <param name="outcome">The outcome of the handler: its returned value, or the exception it threw.</param>
    /// <param name="sp">The scoped service provider.</param>
    /// <param name="ct">The cancellation token.</param>
    private static async Task InvokePostHandleAttributes(IRequest request, RequestOutcome outcome, IServiceProvider sp, CancellationToken ct)
    {
        if (request.Metadata is null) return;

        // Every post-handler receives the request's outcome (the returned value or the thrown exception) and runs on
        // both the success and the exception paths.
        var postHandlers = GetPostHandlers(request);
        for (var i = 0; i < postHandlers.Length; i++)
            await postHandlers[i].OnAfterHandle(request, outcome, sp, ct).ConfigureAwait(false);
    }

    // Command/query path: the interceptors come pre-sorted from the request plan.
    private static async Task InvokePostHandlers(IPostHandlerAttribute[] postHandlers, IRequest request, RequestOutcome outcome, IServiceProvider sp, CancellationToken ct)
    {
        for (var i = 0; i < postHandlers.Length; i++)
            await postHandlers[i].OnAfterHandle(request, outcome, sp, ct).ConfigureAwait(false);
    }

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

    // The exception-path variant: outcome-aware post-handlers also observe a thrown failure, but an audit/post-handler
    // error must never mask the original exception that is about to propagate.
    private static async Task InvokePostHandleAttributesIsolated(IRequest request, RequestOutcome outcome, IServiceProvider sp, CancellationToken ct)
    {
        try
        {
            await InvokePostHandleAttributes(request, outcome, sp, ct).ConfigureAwait(false);
        }
        catch
        {
            // Swallow: the original failure must surface, not a post-handler's.
        }
    }

    private static IPreHandlerAttribute[] GetPreHandlers(IRequest request)
    {
        var metadata = request.Metadata;
        if (metadata is null || metadata.PreHandlers.Length == 0)
            return [];

        var handlers = metadata.PreHandlers;
        if (handlers.Length <= 1)
            return handlers;

        var sorted = SortedPreHandlerCache.GetOrAdd(
            request.GetType(),
            static (_, source) =>
            {
                var clone = (IPreHandlerAttribute[])source.Clone();
                Array.Sort(clone, PreHandlerPriorityComparer.Instance);
                return clone;
            },
            handlers);

        return sorted;
    }

    private static IPostHandlerAttribute[] GetPostHandlers(IRequest request)
    {
        var metadata = request.Metadata;
        if (metadata is null || metadata.PostHandlers.Length == 0)
            return [];

        var handlers = metadata.PostHandlers;
        if (handlers.Length <= 1)
            return handlers;

        var sorted = SortedPostHandlerCache.GetOrAdd(
            request.GetType(),
            static (_, source) =>
            {
                var clone = (IPostHandlerAttribute[])source.Clone();
                Array.Sort(clone, PostHandlerPriorityComparer.Instance);
                return clone;
            },
            handlers);

        return sorted;
    }
}
