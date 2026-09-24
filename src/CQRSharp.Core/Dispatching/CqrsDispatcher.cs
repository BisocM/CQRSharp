using CQRSharp.Core.Modules;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp;

/// <summary>
///     The built-in <see cref="ICqrsDispatcher" />: a per-scope façade that routes requests and streams through the
///     provider-wide route table into this scope's pipeline executor, and hands notifications to the notification
///     dispatcher.
/// </summary>
internal sealed class CqrsDispatcher : ICqrsDispatcher
{
    // Built on first use: a scope that only ever publishes should not also pay for the executor and the routers.
    private readonly IServiceProvider _services;

    // Captured with the scope while it is certainly alive, so building the request path later never reads from it: a
    // queued handler may fire and forget a request that is sent only after that handler's scope is disposed, and that
    // request runs in a scope of its own. (Its context is still the caller's: a custom context factory is resolved from
    // the scope, and fails once the scope is gone.)
    private readonly PipelineExecutorShared _shared;
    private PipelineExecutor? _executor;
    private INotificationDispatcher? _notificationDispatcher;
    private CompositeRequestDispatcher? _requestDispatcher;
    private CompositeStreamRequestDispatcher? _streamRequestDispatcher;

    /// <summary>Creates the façade over a DI scope.</summary>
    /// <param name="services">The current scope's service provider.</param>
    public CqrsDispatcher(IServiceProvider services)
    {
        _services = services;
        _shared = services.GetRequiredService<PipelineExecutorShared>();
    }

    private PipelineExecutor Executor => _executor ??= new PipelineExecutor(_services, _shared);

    private CompositeRequestDispatcher Requests => _requestDispatcher ??= new CompositeRequestDispatcher(_shared.RouteTable, Executor);

    private CompositeStreamRequestDispatcher Streams
        => _streamRequestDispatcher ??= new CompositeStreamRequestDispatcher(_shared.RouteTable, Executor);

    private INotificationDispatcher Notifications
        => _notificationDispatcher ??= _services.GetRequiredService<INotificationDispatcher>();

    /// <inheritdoc />
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request is IStreamRequest)
            throw new InvalidOperationException("Stream requests must be executed via Stream(...) instead of Send(...).");

        return Requests.ExecuteAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request is not IRequest typedRequest)
            throw new ArgumentException($"Request must implement {nameof(IRequest)}.", nameof(request));

        if (typedRequest is IStreamRequest)
            throw new InvalidOperationException("Stream requests must be executed via Stream(...) instead of Send(...).");

        return Requests.ExecuteAsync(typedRequest, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TItem> Stream<TItem>(IStreamRequest<TItem> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Streams.ExecuteAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<object?> Stream(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request is not IStreamRequest typedRequest)
            throw new ArgumentException($"Request must implement {nameof(IStreamRequest)}.", nameof(request));

        return Streams.ExecuteAsync(typedRequest, cancellationToken);
    }

    /// <inheritdoc />
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);
        return Notifications.Publish(notification, cancellationToken);
    }
}
