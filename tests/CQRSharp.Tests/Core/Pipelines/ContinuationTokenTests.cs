using System.Runtime.CompilerServices;
using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     A behavior's <c>next()</c> (the token argument left defaulted) runs the rest of the pipeline under the token the
///     behavior received, for requests, streams and notifications alike; a cancelable token passed to <c>next</c>
///     overrides it. 5.0.0 ran it under <see cref="CancellationToken.None" />, so the handler could not be canceled.
/// </summary>
public sealed class ContinuationTokenTests
{
    [Fact(DisplayName = "A request behavior's next() passes on the token it received")]
    public async Task Request_next_without_a_token_flows_the_received_token()
    {
        await using var provider = Build(services =>
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ParameterlessNextBehavior<,>)));
        await using var scope = provider.CreateAsyncScope();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new ContinuationTokenQuery(), caller.Token);

        ShouldBeTheCallersToken(scope.ServiceProvider.GetRequiredService<ContinuationTokenProbe>().HandlerToken, caller);
    }

    [Fact(DisplayName = "A chain of request behaviors calling next() passes the token on to the handler")]
    public async Task Chained_request_next_without_a_token_flows_the_received_token()
    {
        await using var provider = Build(services =>
        {
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ParameterlessNextBehavior<,>));
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(InnerParameterlessNextBehavior<,>));
        });
        await using var scope = provider.CreateAsyncScope();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new ContinuationTokenQuery(), caller.Token);

        ShouldBeTheCallersToken(scope.ServiceProvider.GetRequiredService<ContinuationTokenProbe>().HandlerToken, caller);
    }

    [Fact(DisplayName = "A token a request behavior passes to next overrides the one it received")]
    public async Task Request_next_with_a_token_overrides_the_received_token()
    {
        await using var provider = Build(services =>
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(OverridingNextBehavior<,>)));
        await using var scope = provider.CreateAsyncScope();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var own = new CancellationTokenSource();
        var probe = scope.ServiceProvider.GetRequiredService<ContinuationTokenProbe>();
        probe.Override = own.Token;

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new ContinuationTokenQuery(), caller.Token);

        probe.HandlerToken.Should().Be(own.Token);
    }

    [Fact(DisplayName = "A stream behavior enumerating next() itself passes on the token it received")]
    public async Task Stream_next_without_a_token_flows_the_received_token()
    {
        await using var provider = Build(services =>
            services.AddTransient(typeof(IStreamPipelineBehavior<,>), typeof(EnumeratingNextStreamBehavior<,>)));
        await using var scope = provider.CreateAsyncScope();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await foreach (var _ in scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Stream(new ContinuationTokenStream(), caller.Token))
        {
        }

        ShouldBeTheCallersToken(scope.ServiceProvider.GetRequiredService<ContinuationTokenProbe>().HandlerToken, caller);
    }

    [Fact(DisplayName = "A notification behavior's next() passes on the token it received")]
    public async Task Notification_next_without_a_token_flows_the_received_token()
    {
        await using var provider = Build(services =>
            services.AddTransient(typeof(INotificationPipelineBehavior<>), typeof(ParameterlessNextNotificationBehavior<>)));
        await using var scope = provider.CreateAsyncScope();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new ContinuationTokenNotification(), caller.Token);

        ShouldBeTheCallersToken(scope.ServiceProvider.GetRequiredService<ContinuationTokenProbe>().HandlerToken, caller);
    }

    private static void ShouldBeTheCallersToken(CancellationToken seen, CancellationTokenSource caller)
    {
        seen.CanBeCanceled.Should().BeTrue("the handler must not run under CancellationToken.None");
        caller.Cancel();
        seen.IsCancellationRequested.Should().BeTrue("canceling the caller's token must reach the handler");
    }

    private static ServiceProvider Build(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        services.AddScoped<ContinuationTokenProbe>();
        configure(services);
        return services.BuildServiceProvider();
    }
}

/// <summary>
///     The token the handler ran under, and the one <see cref="OverridingNextBehavior{TRequest, TResult}" /> passes.
/// </summary>
public sealed class ContinuationTokenProbe
{
    public CancellationToken HandlerToken { get; set; }

    public CancellationToken Override { get; set; }
}

public sealed class ParameterlessNextBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next();
}

public sealed class InnerParameterlessNextBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    public int PipelineExecutionPriority => IPrioritizedPipelineBehavior.DefaultPriority + 1;

    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next();
}

public sealed class OverridingNextBehavior<TRequest, TResult>(ContinuationTokenProbe probe) : IPipelineBehavior<TRequest, TResult>
    where TRequest : IRequest
{
    public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(probe.Override);
}

public sealed class EnumeratingNextStreamBehavior<TRequest, TItem> : IStreamPipelineBehavior<TRequest, TItem> where TRequest : IRequest
{
    // Enumerated without WithCancellation, so the handler sees only the token next() runs it with: the executor's own
    // enumeration cannot hand it the caller's.
    public async IAsyncEnumerable<TItem> Handle(TRequest request, StreamHandlerDelegate<TItem> next, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in next().ConfigureAwait(false))
            yield return item;
    }
}

public sealed class ParameterlessNextNotificationBehavior<TNotification> : INotificationPipelineBehavior<TNotification>
    where TNotification : INotification
{
    public Task Handle(TNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken) => next();
}

public sealed class ContinuationTokenQuery : QueryBase<int>;

public sealed class ContinuationTokenQueryHandler(IServiceProvider services) : IQueryHandler<ContinuationTokenQuery, int>
{
    public Task<int> Handle(ContinuationTokenQuery query, CancellationToken cancellationToken)
    {
        services.GetRequiredService<ContinuationTokenProbe>().HandlerToken = cancellationToken;
        return Task.FromResult(1);
    }
}

public sealed class ContinuationTokenStream : StreamRequestBase<int>;

public sealed class ContinuationTokenStreamHandler(IServiceProvider services) : IStreamRequestHandler<ContinuationTokenStream, int>
{
    public async IAsyncEnumerable<int> Handle(ContinuationTokenStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        services.GetRequiredService<ContinuationTokenProbe>().HandlerToken = cancellationToken;
        await Task.Yield();
        yield return 1;
    }
}

public sealed record ContinuationTokenNotification : INotification;

public sealed class ContinuationTokenNotificationHandler(IServiceProvider services) : INotificationHandler<ContinuationTokenNotification>
{
    public Task Handle(ContinuationTokenNotification notification, CancellationToken cancellationToken)
    {
        services.GetRequiredService<ContinuationTokenProbe>().HandlerToken = cancellationToken;
        return Task.CompletedTask;
    }
}
