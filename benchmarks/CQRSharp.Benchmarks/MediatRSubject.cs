using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Benchmarks.MediatRLib;

public sealed class Ping : IRequest<int>;

public sealed class PingHandler : IRequestHandler<Ping, int>
{
    public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(42);
}

public sealed class PingStream : IStreamRequest<int>;

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

public sealed class PassThroughBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        => next();
}

// MediatR's documented defaults: a transient IMediator and handlers, and a behavior added with AddOpenBehavior (transient).
public static class MediatRSubject
{
    public static ServiceProvider CreateProvider(bool withBehavior)
    {
        var services = new ServiceCollection();
        services.AddMediatR(cfg =>
        {
            // Only this file's handlers: the assembly also holds the other libraries' types.
            cfg.RegisterServicesFromAssemblyContaining<PingHandler>();
            if (withBehavior) cfg.AddOpenBehavior(typeof(PassThroughBehavior<,>));
        });

        return services.BuildServiceProvider();
    }
}
