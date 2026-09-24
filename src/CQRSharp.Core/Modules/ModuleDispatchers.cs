using CQRSharp.Core.Pipelines;

namespace CQRSharp.Core.Modules;

/// <summary>
///     Dispatches a request through its route: one lookup by exact runtime type in the provider-wide
///     <see cref="ModuleRouteTable" />, then a direct call into the executor. Constructing one per scope costs nothing.
/// </summary>
internal sealed class CompositeRequestDispatcher(ModuleRouteTable table, PipelineExecutor pipelineExecutor)
{
    public Task<TResponse> ExecuteAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var route = Route(request.GetType());

        // IRequest<out TResponse> is covariant: a GetUser (IRequest<User>) may arrive as IRequest<object>, and the route's
        // Task<User> is then no Task<object>. Only a non-sealed reference TResponse can be such a widening, so the check
        // costs nothing for the rest; a widened dispatch goes through the boxed route and converts.
        if (!typeof(TResponse).IsValueType && !typeof(TResponse).IsSealed && route.ResultType != typeof(TResponse))
            return Widen<TResponse>(route, request, cancellationToken);

        return (Task<TResponse>)route.Execute(pipelineExecutor, request, cancellationToken);
    }

    public Task<object?> ExecuteAsync(IRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Route(request.GetType()).ExecuteBoxed(pipelineExecutor, request, cancellationToken);
    }

    private async Task<TResponse> Widen<TResponse>(RequestRoute route, IRequest request, CancellationToken cancellationToken)
        => (TResponse)(await route.ExecuteBoxed(pipelineExecutor, request, cancellationToken).ConfigureAwait(false))!;

    private RequestRoute Route(Type requestType)
        => table.Routes.TryGetValue(requestType, out var route)
            ? route
            : throw new InvalidOperationException(
                $"No handler or pipeline found for request type '{requestType.FullName}'. Ensure it's public or internal, has a " +
                "corresponding handler, and its assembly's CQRSharp module is registered.");
}

/// <summary>
///     Dispatches a streaming request through its route: one lookup by exact runtime type in the provider-wide
///     <see cref="ModuleRouteTable" />.
/// </summary>
internal sealed class CompositeStreamRequestDispatcher(ModuleRouteTable table, PipelineExecutor pipelineExecutor)
{
    public IAsyncEnumerable<TItem> ExecuteAsync<TItem>(IStreamRequest<TItem> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (IAsyncEnumerable<TItem>)Route(request.GetType()).Execute(pipelineExecutor, request, cancellationToken);
    }

    public IAsyncEnumerable<object?> ExecuteAsync(IStreamRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Route(request.GetType()).ExecuteBoxed(pipelineExecutor, request, cancellationToken);
    }

    private StreamRoute Route(Type requestType)
        => table.StreamRoutes.TryGetValue(requestType, out var route)
            ? route
            : throw new InvalidOperationException(
                $"No stream handler or pipeline found for request type '{requestType.FullName}'. Ensure it's public or internal, " +
                "has a corresponding handler, and its assembly's CQRSharp module is registered.");
}
