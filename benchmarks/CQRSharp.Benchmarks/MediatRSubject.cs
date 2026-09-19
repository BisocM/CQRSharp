using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Benchmarks.MediatRLib;

public sealed class Ping : IRequest<int>;

public sealed class PingHandler : IRequestHandler<Ping, int>
{
    public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(42);
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

    public static IMediator Create(bool withBehavior)
        => CreateProvider(withBehavior).CreateScope().ServiceProvider.GetRequiredService<IMediator>();
}
