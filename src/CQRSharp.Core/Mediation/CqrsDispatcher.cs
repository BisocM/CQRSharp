using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Interfaces.Markers.Stream;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Mediation;

/// <summary>
///     Default implementation of <see cref="ICqrsDispatcher" /> backed by CQRSharp dispatchers.
/// </summary>
public sealed class CqrsDispatcher : ICqrsDispatcher
{
    // Resolved on first use when created from a provider: a scope that only ever sends a command should not also pay to
    // build the streaming and notification dispatchers (and everything they depend on).
    private readonly IServiceProvider? _services;
    private INotificationDispatcher? _notificationDispatcher;
    private IRequestDispatcher? _requestDispatcher;
    private IStreamRequestDispatcher? _streamRequestDispatcher;

    /// <summary>
    ///     Creates the façade over a DI scope; each underlying dispatcher is resolved from it the first time it is needed.
    /// </summary>
    /// <param name="services">The current scope's service provider.</param>
    public CqrsDispatcher(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    private IRequestDispatcher Requests
        => _requestDispatcher ??= _services!.GetRequiredService<IRequestDispatcher>();

    private IStreamRequestDispatcher Streams
        => _streamRequestDispatcher ??= _services!.GetRequiredService<IStreamRequestDispatcher>();

    private INotificationDispatcher Notifications
        => _notificationDispatcher ??= _services!.GetRequiredService<INotificationDispatcher>();

    /// <summary>
    ///     Initializes a new <see cref="CqrsDispatcher" /> that forwards requests, streams, and notifications
    ///     to the supplied underlying dispatchers.
    /// </summary>
    /// <param name="requestDispatcher">Dispatcher used to execute non-streaming requests.</param>
    /// <param name="streamRequestDispatcher">Dispatcher used to execute streaming requests.</param>
    /// <param name="notificationDispatcher">Dispatcher used to publish notifications.</param>
    public CqrsDispatcher(
        IRequestDispatcher requestDispatcher,
        IStreamRequestDispatcher streamRequestDispatcher,
        INotificationDispatcher notificationDispatcher)
    {
        _requestDispatcher = requestDispatcher ?? throw new ArgumentNullException(nameof(requestDispatcher));
        _streamRequestDispatcher = streamRequestDispatcher ?? throw new ArgumentNullException(nameof(streamRequestDispatcher));
        _notificationDispatcher = notificationDispatcher ?? throw new ArgumentNullException(nameof(notificationDispatcher));
    }

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