using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Interfaces.Notifications;

namespace CQRSharp.Tests.ExternalModule;

/// <summary>An observable side-channel so a test in another assembly can confirm this assembly's handlers ran.</summary>
public static class ExternalSignals
{
    public static int NotificationHandled;

    public static void Reset() => NotificationHandled = 0;
}

/// <summary>A query declared in a separate assembly, dispatched from the composition root to prove cross-assembly routing.</summary>
public sealed class ExternalQuery : QueryBase<string>
{
    public required string Value { get; init; }
}

/// <summary>
///     Deliberately <see langword="internal" />: the composition root cannot reference this type, so it could not
///     register it directly. It is registered because THIS assembly's generated module registers it — the guarantee the
///     per-assembly module design provides.
/// </summary>
internal sealed class ExternalQueryHandler : IQueryHandler<ExternalQuery, string>
{
    public Task<string> Handle(ExternalQuery query, CancellationToken cancellationToken)
        => Task.FromResult($"external:{query.Value}");
}

/// <summary>A notification declared in a separate assembly, to prove cross-assembly notification dispatch.</summary>
public sealed class ExternalNotification : INotification
{
    public required string Message { get; init; }
}

/// <summary>Internal handler for <see cref="ExternalNotification" />; signals via <see cref="ExternalSignals" />.</summary>
internal sealed class ExternalNotificationHandler : INotificationHandler<ExternalNotification>
{
    public Task Handle(ExternalNotification notification, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ExternalSignals.NotificationHandled);
        return Task.CompletedTask;
    }
}
