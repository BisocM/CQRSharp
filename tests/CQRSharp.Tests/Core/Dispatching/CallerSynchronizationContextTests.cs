using System.Diagnostics;
using CQRSharp.Core.Diagnostics;
using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Dispatch never resumes on its caller's <see cref="SynchronizationContext" />. A continuation posted back to a UI
///     thread (or any single-threaded context) deadlocks a caller that blocks on the call, and runs library code on that
///     thread otherwise. Each test starts the work under a <see cref="PostCountingSynchronizationContext" />, holds a
///     disposal the library awaits open until the work has left the caller's frame, and checks nothing was posted back.
/// </summary>
/// <remarks>
///     The traced case subscribes an <see cref="ActivityListener" />, which changes the dispatch path of every test running
///     beside it, so the class runs in the <see cref="TracingCollection" />.
/// </remarks>
[Collection(TracingCollection.Name)]
public sealed class CallerSynchronizationContextTests
{
    [Fact(DisplayName = "A request in a scope of its own never resumes on its caller's context while the scope's disposal completes asynchronously")]
    public async Task A_request_in_its_own_scope_never_resumes_on_the_callers_context()
    {
        await using var provider = Build(_ => { }, services => services.Configure<DispatcherOptions>(o => o.ScopeMode = ExecutionScopeMode.New));
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var gate = provider.GetRequiredService<ResumptionGate>();
        var context = new PostCountingSynchronizationContext();

        Task<CommandResult> sending;
        using (context.Install())
            sending = dispatcher.Send(new ResumptionCommand(), TestContext.Current.CancellationToken);

        sending.IsCompleted.Should().BeFalse("the request's scope is still disposing its dependency");
        gate.Open();
        (await sending).IsSuccess.Should().BeTrue();
        context.Posts.Should().Be(0);
    }

    [Theory(DisplayName = "A stream never resumes on its caller's context while its handler's enumerator disposes asynchronously")]
    [InlineData(StreamPath.Logging)]
    [InlineData(StreamPath.ExceptionHandling)]
    [InlineData(StreamPath.Validation)]
    [InlineData(StreamPath.Resilience)]
    [InlineData(StreamPath.Idempotency)]
    [InlineData(StreamPath.UnitOfWork)]
    [InlineData(StreamPath.Timeout)]
    [InlineData(StreamPath.OwnScope)]
    [InlineData(StreamPath.Traced)]
    public async Task A_stream_never_resumes_on_the_callers_context(StreamPath path)
    {
        await using var provider = Build(
            builder => Configure(builder, path),
            services =>
            {
                if (path == StreamPath.OwnScope)
                    services.Configure<DispatcherOptions>(o => o.ScopeMode = ExecutionScopeMode.New);
            });
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var gate = provider.GetRequiredService<ResumptionGate>();
        using var listener = path == StreamPath.Traced ? Listen() : null;
        var context = new PostCountingSynchronizationContext();

        var enumerator = dispatcher.Stream(new ResumptionStream(), TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        ValueTask<bool> first, end;
        using (context.Install())
        {
            first = enumerator.MoveNextAsync();
            end = first.IsCompletedSuccessfully ? enumerator.MoveNextAsync() : default;
        }

        first.IsCompletedSuccessfully.Should().BeTrue("the stream yields its item without an asynchronous step");
        end.IsCompleted.Should().BeFalse("the handler's enumerator is still being disposed");
        gate.Open();
        (await end).Should().BeFalse();
        await enumerator.DisposeAsync();
        context.Posts.Should().Be(0);
    }

    public enum StreamPath
    {
        Logging,
        ExceptionHandling,
        Validation,
        Resilience,
        Idempotency,
        UnitOfWork,
        Timeout,
        OwnScope,
        Traced
    }

    // One built-in stream behavior on its own, or none (the executor's full path alone).
    private static void Configure(ICqrsBuilder builder, StreamPath path)
    {
        builder.UseValidation(path == StreamPath.Validation).UseExceptionHandling(path == StreamPath.ExceptionHandling);
        switch (path)
        {
            case StreamPath.Logging:
                builder.UseLogging();
                break;
            case StreamPath.Resilience:
                builder.UseResilience(_ => { });
                break;
            case StreamPath.Idempotency:
                builder.UseIdempotency();
                break;
            case StreamPath.UnitOfWork:
                builder.UseUnitOfWork(_ => new RecordingUnitOfWork());
                break;
            case StreamPath.Timeout:
                builder.UseTimeout(_ => { });
                break;
        }
    }

    private static ServiceProvider Build(Action<ICqrsBuilder> configure, Action<IServiceCollection> configureServices)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddSingleton<ResumptionGate>();
        services.AddScoped<SlowlyDisposedDependency>();
        configureServices(services);
        services.AddCqrsGenerated(builder => configure(builder.UseValidation(false)));
        return services.BuildServiceProvider();
    }

    private static ActivityListener Listen()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CqrsTelemetry.ActivitySourceName,
            Sample = (ref _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

/// <summary>A disposal that completes only once a test opens it, after the work under test has left its caller's frame.</summary>
public sealed class ResumptionGate
{
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask Disposal => new(_opened.Task);

    public void Open() => _opened.TrySetResult();
}

/// <summary>A scoped dependency whose disposal completes asynchronously, as a pooled connection's can.</summary>
public sealed class SlowlyDisposedDependency(ResumptionGate gate) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => gate.Disposal;
}

public sealed class ResumptionCommand : CommandBase;

public sealed class ResumptionCommandHandler(SlowlyDisposedDependency dependency) : ICommandHandler<ResumptionCommand>
{
    public Task<CommandResult> Handle(ResumptionCommand command, CancellationToken cancellationToken)
    {
        // Resolving the dependency is the point: the request's scope disposes it once the handler is done.
        _ = dependency;
        return Task.FromResult(CommandResult.FromSuccess());
    }
}

/// <summary>A stream every built-in stream behavior applies to, once it is enabled.</summary>
public sealed class ResumptionStream : StreamRequestBase<int>, IIdempotentRequest, IRetryableRequest, ITransactionalQuery
{
    public string IdempotencyKey { get; } = Guid.NewGuid().ToString("N");
    public System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.Unspecified;
    public bool IsReadOnly => false;
}

/// <summary>Yields one item, then ends; its enumerator's disposal completes when the gate opens.</summary>
public sealed class ResumptionStreamHandler(ResumptionGate gate) : IStreamRequestHandler<ResumptionStream, int>
{
    public IAsyncEnumerable<int> Handle(ResumptionStream request, CancellationToken cancellationToken) => new SlowlyDisposedStream(gate);

    private sealed class SlowlyDisposedStream(ResumptionGate gate) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private int _position;

        public int Current => 1;

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public ValueTask<bool> MoveNextAsync() => new(++_position == 1);

        public ValueTask DisposeAsync() => gate.Disposal;
    }
}

// An exception hook, so the exception-handling behavior wraps the stream.
public sealed class ResumptionStreamFailureAction : IRequestExceptionAction<ResumptionStream, InvalidOperationException>
{
    public Task Execute(ResumptionStream request, InvalidOperationException exception, CancellationToken cancellationToken) => Task.CompletedTask;
}
