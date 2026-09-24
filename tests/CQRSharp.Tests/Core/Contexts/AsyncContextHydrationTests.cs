using CQRSharp.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     A context factory that hydrates asynchronously is awaited once, before the pipeline runs: the handler of a
///     command, a query or a stream never starts before its context is there, on the fast path and on the measured path
///     alike, and the context is stamped from the application's clock as a synchronously created one is. The factory
///     holds each request's hydration until the test releases it, so the dispatcher genuinely has to wait for it.
/// </summary>
public sealed class AsyncContextHydrationTests
{
    private static readonly DateTimeOffset Now = new(2031, 5, 4, 3, 2, 1, TimeSpan.Zero);

    [Theory(DisplayName = "A query's handler runs only once its asynchronously hydrated context is there, stamped from the application's clock")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_handler_sees_the_hydrated_context(bool measured)
    {
        await using var provider = Build();
        using var measuring = measured ? MeasureRequests(provider) : null;
        await using var scope = provider.CreateAsyncScope();
        var hydration = new TaskCompletionSource();
        var query = new GatedQuery { Hydration = hydration.Task };

        var sent = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(query, TestContext.Current.CancellationToken);

        query.Handled.Should().BeFalse("the handler waits for its context");
        hydration.SetResult();
        (await sent).Should().Be("hydrated");
        query.ContextAtHandle.Should().NotBeNull().And.BeSameAs(query.Context);
        query.Context!.CreatedAt.Should().Be(Now.UtcDateTime);
        measuring?.Measurements.Should().ContainSingle("the measured path ran");
    }

    [Theory(DisplayName = "A command's handler runs only once its asynchronously hydrated context is there")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Command_handler_sees_the_hydrated_context(bool measured)
    {
        await using var provider = Build();
        using var measuring = measured ? MeasureRequests(provider) : null;
        await using var scope = provider.CreateAsyncScope();
        var hydration = new TaskCompletionSource();
        var command = new GatedCommand { Hydration = hydration.Task };

        var sent = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(command, TestContext.Current.CancellationToken);

        command.Handled.Should().BeFalse("the handler waits for its context");
        hydration.SetResult();
        (await sent).IsSuccess.Should().BeTrue();
        command.ContextAtHandle.Should().NotBeNull().And.BeSameAs(command.Context);
        measuring?.Measurements.Should().ContainSingle("the measured path ran");
    }

    // With nothing around its handler, a stream is otherwise handed to its consumer as the handler's own: a context that
    // still has to be awaited is what keeps it off that path, so the handler is not called before the context exists.
    [Theory(DisplayName = "A stream's handler is called only once its asynchronously hydrated context is there")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stream_handler_sees_the_hydrated_context(bool measured)
    {
        await using var provider = Build();
        using var measuring = measured ? MeasureRequests(provider) : null;
        await using var scope = provider.CreateAsyncScope();
        var hydration = new TaskCompletionSource();
        var request = new GatedStream { Hydration = hydration.Task };

        await using var items = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Stream(request, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var first = items.MoveNextAsync();

        request.Handled.Should().BeFalse("the handler waits for its context");
        hydration.SetResult();
        (await first).Should().BeTrue();
        items.Current.Should().Be("hydrated");
        request.ContextAtHandle.Should().NotBeNull().And.BeSameAs(request.Context);
        (await items.MoveNextAsync()).Should().BeFalse();
        measuring?.Measurements.Should().ContainSingle("the measured path ran");
    }

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        services.AddCqrsGenerated();
        return services.BuildServiceProvider();
    }

    // Measuring the request duration is what moves a dispatch off the fast path onto the measured one.
    private static InstrumentRecorder<double> MeasureRequests(IServiceProvider provider)
        => new(provider, CqrsTelemetry.Instruments.RequestDuration);
}

/// <summary>A request whose context the <see cref="GatedContextFactory" /> hydrates once this task completes.</summary>
public interface IHydrationGated
{
    Task Hydration { get; }
}

// Built with the parameterless constructor, as a factory usually builds one: the dispatcher stamps its timestamp.
public sealed class GatedContext : RequestContextBase
{
    public string Source { get; init; } = "";
}

public sealed class GatedContextFactory : IRequestContextFactory<GatedContext>
{
    public async ValueTask<GatedContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken)
    {
        if (request is IHydrationGated gated)
            await gated.Hydration.WaitAsync(cancellationToken);
        else
            await Task.Yield();

        return new GatedContext { Source = "hydrated" };
    }
}

public sealed class GatedQuery : QueryBase<string, GatedContext>, IHydrationGated
{
    public Task Hydration { get; init; } = Task.CompletedTask;
    public bool Handled { get; set; }
    public GatedContext? ContextAtHandle { get; set; }
}

public sealed class GatedQueryHandler : IQueryHandler<GatedQuery, string>
{
    public Task<string> Handle(GatedQuery query, CancellationToken cancellationToken)
    {
        query.Handled = true;
        query.ContextAtHandle = query.Context;
        return Task.FromResult(query.Context?.Source ?? "none");
    }
}

public sealed class GatedCommand : CommandBase<GatedContext>, IHydrationGated
{
    public Task Hydration { get; init; } = Task.CompletedTask;
    public bool Handled { get; set; }
    public GatedContext? ContextAtHandle { get; set; }
}

public sealed class GatedCommandHandler : ICommandHandler<GatedCommand>
{
    public Task<CommandResult> Handle(GatedCommand command, CancellationToken cancellationToken)
    {
        command.Handled = true;
        command.ContextAtHandle = command.Context;
        return Task.FromResult(CommandResult.FromSuccess());
    }
}

public sealed class GatedStream : StreamRequestBase<string, GatedContext>, IHydrationGated
{
    public Task Hydration { get; init; } = Task.CompletedTask;
    public bool Handled { get; set; }
    public GatedContext? ContextAtHandle { get; set; }
}

public sealed class GatedStreamHandler : IStreamRequestHandler<GatedStream, string>
{
    // Not an iterator, so what it records is the context at the moment the dispatcher calls it, not at the first MoveNext.
    public IAsyncEnumerable<string> Handle(GatedStream request, CancellationToken cancellationToken)
    {
        request.Handled = true;
        request.ContextAtHandle = request.Context;
        return Produce(request.Context?.Source ?? "none");

        static async IAsyncEnumerable<string> Produce(string source)
        {
            await Task.Yield();
            yield return source;
        }
    }
}
