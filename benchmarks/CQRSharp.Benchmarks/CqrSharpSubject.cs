using CQRSharp;
using CQRSharp.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace Benchmarks.CqrSharp;

public sealed class Ping : QueryBase<int>;

public sealed class PingHandler : IQueryHandler<Ping, int>
{
    public Task<int> Handle(Ping query, CancellationToken cancellationToken) => Task.FromResult(42);
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

public static class CqrSharpSubject
{
    public static ServiceProvider CreateProvider(bool withBehavior)
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        if (withBehavior)
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(PassThroughBehavior<,>));

        // Every subject dispatches from one long-lived scope, mirroring a request scope in a real host.
        return services.BuildServiceProvider();
    }

    public static ICqrsDispatcher Create(bool withBehavior)
        => CreateProvider(withBehavior).CreateScope().ServiceProvider.GetRequiredService<ICqrsDispatcher>();
}
