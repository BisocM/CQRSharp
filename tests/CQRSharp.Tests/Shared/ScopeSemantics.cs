using System.Collections.Concurrent;
using CQRSharp.Abstractions.Data.Interfaces.Handlers;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using CQRSharp.Core.Mediation;

namespace CQRSharp.Tests.Shared;

public sealed class ScopedMarker
{
    public Guid Id { get; } = Guid.NewGuid();
}

public sealed class GetScopedMarkerIdQuery : QueryBase<Guid>;

public sealed class GetScopedMarkerIdQueryHandler(ScopedMarker marker) : IQueryHandler<GetScopedMarkerIdQuery, Guid>
{
    public Task<Guid> Handle(GetScopedMarkerIdQuery query, CancellationToken cancellationToken)
        => Task.FromResult(marker.Id);
}

public sealed class NestedSendScopeQuery : QueryBase<bool>;

public sealed class NestedSendScopeQueryHandler(ScopedMarker marker, ICqrsDispatcher cqrs) : IQueryHandler<NestedSendScopeQuery, bool>
{
    public async Task<bool> Handle(NestedSendScopeQuery query, CancellationToken cancellationToken)
    {
        var innerId = await cqrs.Send(new GetScopedMarkerIdQuery(), cancellationToken);
        return innerId == marker.Id;
    }
}

public sealed class ScopeCheckSink
{
    private readonly ConcurrentQueue<bool> _results = new();

    public void Add(bool result) => _results.Enqueue(result);

    public bool TryTake(out bool result) => _results.TryDequeue(out result);
}

public sealed record ScopeCheckNotification(Guid Expected) : INotification;

public sealed class ScopeCheckNotificationHandler(ScopedMarker marker, ScopeCheckSink sink) : INotificationHandler<ScopeCheckNotification>
{
    public Task Handle(ScopeCheckNotification notification, CancellationToken cancellationToken)
    {
        sink.Add(notification.Expected == marker.Id);
        return Task.CompletedTask;
    }
}

public sealed class PublishScopeCheckQuery : QueryBase<bool>;

public sealed class PublishScopeCheckQueryHandler(ICqrsDispatcher cqrs, ScopedMarker marker, ScopeCheckSink sink)
    : IQueryHandler<PublishScopeCheckQuery, bool>
{
    public async Task<bool> Handle(PublishScopeCheckQuery query, CancellationToken cancellationToken)
    {
        await cqrs.Publish(new ScopeCheckNotification(marker.Id), cancellationToken);
        return sink.TryTake(out var ok) && ok;
    }
}
