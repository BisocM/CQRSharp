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
