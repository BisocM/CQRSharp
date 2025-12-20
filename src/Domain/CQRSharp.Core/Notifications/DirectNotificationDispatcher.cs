using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Core.Notifications;

/// <summary>
///     Implements <see cref="IDirectNotificationDispatcher" /> to dispatch notifications directly to handlers.
///     This implementation resolves handlers from a new service scope and awaits their execution.
/// </summary>
public sealed class DirectNotificationDispatcher : IDirectNotificationDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DirectNotificationDispatcher" /> class.
    /// </summary>
    /// <param name="scopeFactory">The service scope factory to create scopes for resolving handlers.</param>
    public DirectNotificationDispatcher(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <inheritdoc />
    public Task Publish(INotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        throw new NotSupportedException(
            "Untyped notification dispatch requires the CQRSharp source generator. " +
            "Ensure you call services.AddGenerated() during startup so an AOT-safe dispatcher is registered.");
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

        List<Task>? tasks = null;
        foreach (var handler in handlers)
        {
            tasks ??= [];
            tasks.Add(handler.Handle(notification, cancellationToken));
        }

        if (tasks is null) return;

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
