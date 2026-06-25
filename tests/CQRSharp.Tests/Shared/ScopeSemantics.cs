using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CQRSharp.Abstractions.Interfaces.Handlers;
using CQRSharp.Abstractions.Interfaces.Markers.Query;
using CQRSharp.Abstractions.Interfaces.Markers.Stream;
using CQRSharp.Abstractions.Interfaces.Notifications;
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

public sealed class NestedSendScopeStreamRequest : StreamRequestBase<bool>;

public sealed class NestedSendScopeStreamRequestHandler(ScopedMarker marker, ICqrsDispatcher cqrs)
    : IStreamRequestHandler<NestedSendScopeStreamRequest, bool>
{
    public async IAsyncEnumerable<bool> Handle(NestedSendScopeStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var innerId = await cqrs.Send(new GetScopedMarkerIdQuery(), cancellationToken);
        yield return innerId == marker.Id;
    }
}

public sealed class DisposalTracker
{
    private int _disposedCount;

    public int DisposedCount => Volatile.Read(ref _disposedCount);

    public void RecordDisposed() => Interlocked.Increment(ref _disposedCount);
}

public sealed class ScopedDisposalProbe(DisposalTracker tracker) : IAsyncDisposable
{
    public Guid Id { get; } = Guid.NewGuid();

    public ValueTask DisposeAsync()
    {
        tracker.RecordDisposed();
        return default;
    }
}

public sealed class StreamScopeDisposalRequest : StreamRequestBase<Guid>;

public sealed class StreamScopeDisposalRequestHandler(ScopedDisposalProbe probe) : IStreamRequestHandler<StreamScopeDisposalRequest, Guid>
{
    public async IAsyncEnumerable<Guid> Handle(StreamScopeDisposalRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return probe.Id;
        await Task.Delay(50, cancellationToken);
        yield return probe.Id;
    }
}