using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Benchmarks.MediatorLib;

public sealed class Ping : IRequest<int>;

public sealed class PingHandler : IRequestHandler<Ping, int>
{
    public ValueTask<int> Handle(Ping request, CancellationToken cancellationToken) => new(42);
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
    public ValueTask Handle(Pinged notification, CancellationToken cancellationToken) => default;
}

public sealed class PassThroughBehavior<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse> where TMessage : IMessage
{
    public ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
        => next(message, cancellationToken);
}

// Mediator's documented defaults: the Singleton lifetime (its IMediator and handlers are singletons, the configuration
// its README recommends for performance) and a behavior registered with AddSingleton. Its PipelineBehaviors option is not
// used: it is read at compile time, so it would apply to the provider without the behavior as well.
public static class MediatorSubject
{
    public static ServiceProvider CreateProvider(bool withBehavior)
    {
        var services = new ServiceCollection();
        services.AddMediator();
        if (withBehavior)
            services.AddSingleton(typeof(IPipelineBehavior<,>), typeof(PassThroughBehavior<,>));

        return services.BuildServiceProvider();
    }
}
