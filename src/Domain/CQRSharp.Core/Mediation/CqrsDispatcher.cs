using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Pipelines;

namespace CQRSharp.Core.Mediation;

/// <summary>
///     Default implementation of <see cref="ICqrsDispatcher" /> backed by CQRSharp dispatchers.
/// </summary>
public sealed class CqrsDispatcher : ICqrsDispatcher
{
    private readonly IRequestDispatcher _requestDispatcher;
    private readonly INotificationDispatcher _notificationDispatcher;

    public CqrsDispatcher(
        IRequestDispatcher requestDispatcher,
        INotificationDispatcher notificationDispatcher)
    {
        _requestDispatcher = requestDispatcher ?? throw new ArgumentNullException(nameof(requestDispatcher));
        _notificationDispatcher = notificationDispatcher ?? throw new ArgumentNullException(nameof(notificationDispatcher));
    }

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _requestDispatcher.ExecuteAsync(request, cancellationToken);
    }

    public Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request is not IRequest typedRequest)
            throw new ArgumentException($"Request must implement {nameof(IRequest)}.", nameof(request));

        return _requestDispatcher.ExecuteAsync(typedRequest, cancellationToken);
    }

    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);
        return _notificationDispatcher.Publish(notification, cancellationToken);
    }
}
