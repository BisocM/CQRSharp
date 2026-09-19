using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Benchmarks.MediatorLib;

public sealed class Ping : IRequest<int>;

public sealed class PingHandler : IRequestHandler<Ping, int>
{
    public ValueTask<int> Handle(Ping request, CancellationToken cancellationToken) => new(42);
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

public static class MediatorSubject
{
    public static ServiceProvider CreateProvider(bool withBehavior)
    {
        var services = new ServiceCollection();
        services.AddMediator(options => options.ServiceLifetime = ServiceLifetime.Scoped);
        if (withBehavior)
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(PassThroughBehavior<,>));

        return services.BuildServiceProvider();
    }

    public static IMediator Create(bool withBehavior)
        => CreateProvider(withBehavior).CreateScope().ServiceProvider.GetRequiredService<IMediator>();
}
