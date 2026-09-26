using CQRSharp;
using CQRSharp.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace Benchmarks.CqrSharp;

public sealed class Ping : QueryBase<int>;

public sealed class PingHandler : IQueryHandler<Ping, int>
{
    public Task<int> Handle(Ping query, CancellationToken cancellationToken) => Task.FromResult(42);
}

public sealed class PingStream : StreamRequestBase<int>;

public sealed class PingStreamHandler : IStreamRequestHandler<PingStream, int>
{
    public async IAsyncEnumerable<int> Handle(PingStream request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        yield return 2;
        yield return 3;
        await Task.CompletedTask;
    }
}

// A custom context, as an application carries the caller's tenant: one factory builds it with the parameterless
// constructor, which the dispatcher then stamps, the other passes the application's time itself.
public sealed class TenantContext : RequestContextBase
{
    public required string Tenant { get; init; }
}

public sealed class TimedTenantContext(DateTime createdAt) : RequestContextBase(createdAt)
{
    public required string Tenant { get; init; }
}

public sealed class TenantContextFactory : IRequestContextFactory<TenantContext>
{
    public ValueTask<TenantContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken)
        => new(new TenantContext { Tenant = "acme" });
}

public sealed class TimedTenantContextFactory(TimeProvider timeProvider) : IRequestContextFactory<TimedTenantContext>
{
    public ValueTask<TimedTenantContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken)
        => new(new TimedTenantContext(timeProvider.GetUtcNow().UtcDateTime) { Tenant = "acme" });
}

public sealed class PingWithTenant : QueryBase<int, TenantContext>;

public sealed class PingWithTenantHandler : IQueryHandler<PingWithTenant, int>
{
    public Task<int> Handle(PingWithTenant query, CancellationToken cancellationToken) => Task.FromResult(42);
}

public sealed class PingWithTimedTenant : QueryBase<int, TimedTenantContext>;

public sealed class PingWithTimedTenantHandler : IQueryHandler<PingWithTimedTenant, int>
{
    public Task<int> Handle(PingWithTimedTenant query, CancellationToken cancellationToken) => Task.FromResult(42);
}

public sealed record Pinged : INotification;

public sealed class PingedHandler : INotificationHandler<Pinged>
{
    public Task Handle(Pinged notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class PassThroughBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken)
        => next(cancellationToken);
}

// CQRSharp's documented defaults: a scoped ICqrsDispatcher, transient handlers, and a behavior registered as a transient
// open generic.
public static class CqrSharpSubject
{
    public static ServiceProvider CreateProvider(bool withBehavior)
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        if (withBehavior)
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(PassThroughBehavior<,>));

        return services.BuildServiceProvider();
    }
}
