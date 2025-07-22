using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     Dispatches notifications to all registered <see cref="INotificationHandler{TNotification}" /> implementations.
/// </summary>
public sealed class NotificationDispatcher : INotificationDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    ///     Constructs a new dispatcher.
    /// </summary>
    /// <param name="scopeFactory">Used to create a new DI scope per notification publish.</param>
    public NotificationDispatcher(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <inheritdoc />
    public async Task Publish<TNotification>(
        TNotification notification,
        CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        using var scope = _scopeFactory.CreateScope();

        var handlers = scope
            .ServiceProvider
            .GetServices<INotificationHandler<TNotification>>();

        var tasks = handlers
            .Select(h => h.Handle(notification, cancellationToken));

        //Let ANY exception (other than cancellation) bubble out as an AggregateException
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}