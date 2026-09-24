using CQRSharp.Pipelines;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Exception actions and exception handlers for requests and streams: every action for a thrown exception's type or
///     a base type runs whether or not a handler then handles it, and the most specific handler turns the exception
///     into a response or a replacement stream.
/// </summary>
public sealed class ExceptionPipelineHookTests
{
    [Fact]
    public async Task Exception_actions_run_even_when_exception_not_handled()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseValidation(false));
        services.AddSingleton<ExceptionHookProbe>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = scope.ServiceProvider.GetRequiredService<ExceptionHookProbe>();

        var act = async () => await cqrs.Send(new ActionOnlyExceptionCommand());

        await act.Should().ThrowAsync<ActionOnlyException>();
        probe.ActionCalls.Should().Be(1);
    }

    [Fact(DisplayName = "An exception handler converts the exception into the response, and that is logged at Information")]
    public async Task Exception_handlers_can_convert_exception_into_response()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseValidation(false));
        services.AddSingleton<ExceptionHookProbe>();
        var logger = new CapturingLogger<ExceptionHandlingBehavior<HandledExceptionCommand, CommandResult>>();
        services.AddSingleton<ILogger<ExceptionHandlingBehavior<HandledExceptionCommand, CommandResult>>>(logger);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = scope.ServiceProvider.GetRequiredService<ExceptionHookProbe>();

        var result = await cqrs.Send(new HandledExceptionCommand(), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("handled");
        probe.HandledHandlerCalls.Should().Be(1);
        logger.Entries.Should().ContainSingle().Which.Should().Match<CapturedLogEntry>(e =>
            e.EventId.Id == 4600 && e.Level == LogLevel.Information && e.Exception == null && e.Message.Contains(nameof(HandledException)));
    }

    [Fact]
    public async Task Most_specific_exception_handler_executes_first()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseValidation(false));
        services.AddSingleton<ExceptionHookProbe>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = scope.ServiceProvider.GetRequiredService<ExceptionHookProbe>();

        var result = await cqrs.Send(new DerivedExceptionCommand(), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("derived");
        probe.DerivedHandlerCalls.Should().Be(1);
        probe.BaseHandlerCalls.Should().Be(0);
    }

    [Fact]
    public async Task Stream_exception_actions_run_even_when_exception_not_handled()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseValidation(false));
        services.AddSingleton<ExceptionHookProbe>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = scope.ServiceProvider.GetRequiredService<ExceptionHookProbe>();

        var act = async () =>
        {
            await foreach (var _ in cqrs.Stream(new ActionOnlyExceptionStreamRequest()))
            {
            }
        };

        await act.Should().ThrowAsync<ActionOnlyException>();
        probe.ActionCalls.Should().Be(1);
    }

    [Fact(DisplayName = "An exception handler converts a stream's exception into the rest of the stream, and that is logged at Information")]
    public async Task Stream_exception_handlers_can_convert_exception_into_stream()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseValidation(false));
        services.AddSingleton<ExceptionHookProbe>();
        var logger = new CapturingLogger<StreamExceptionHandlingBehavior<HandledExceptionStreamRequest, int>>();
        services.AddSingleton<ILogger<StreamExceptionHandlingBehavior<HandledExceptionStreamRequest, int>>>(logger);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = scope.ServiceProvider.GetRequiredService<ExceptionHookProbe>();

        var results = new List<int>();
        await foreach (var item in cqrs.Stream(new HandledExceptionStreamRequest(), TestContext.Current.CancellationToken))
            results.Add(item);

        results.Should().Equal(42, 43);
        probe.HandledHandlerCalls.Should().Be(1);
        logger.Entries.Should().ContainSingle().Which.Should().Match<CapturedLogEntry>(e =>
            e.EventId.Id == 4601 && e.Level == LogLevel.Information && e.Exception == null && e.Message.Contains(nameof(HandledException)));
    }

    [Fact]
    public async Task Stream_uses_most_specific_exception_handler_first()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseValidation(false));
        services.AddSingleton<ExceptionHookProbe>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var cqrs = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = scope.ServiceProvider.GetRequiredService<ExceptionHookProbe>();

        var results = new List<int>();
        await foreach (var item in cqrs.Stream(new DerivedExceptionStreamRequest(), TestContext.Current.CancellationToken))
            results.Add(item);

        results.Should().Equal(1);
        probe.DerivedHandlerCalls.Should().Be(1);
        probe.BaseHandlerCalls.Should().Be(0);
    }

    [Fact(DisplayName = "An exception action for a base exception type runs even when a handler for the derived type handles it")]
    public async Task Exception_actions_run_before_a_handler_swallows_the_exception()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseValidation(false));
        services.AddSingleton<ExceptionHookProbe>();
        services.AddSingleton<BaseActionProbe>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new DerivedExceptionCommand(), TestContext.Current.CancellationToken);

        result.ErrorMessage.Should().Be("derived");
        provider.GetRequiredService<BaseActionProbe>().Calls.Should().Be(1);
    }

    [Fact(DisplayName = "A cancellation the caller did not ask for (a dependency's timeout) reaches the exception handlers like any other failure")]
    public async Task Foreign_cancellation_reaches_the_exception_handlers()
    {
        await using var provider = BuildWithProbe();
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new CancelledDependencyCommand(), TestContext.Current.CancellationToken);

        result.ErrorMessage.Should().Be("fallback");
        scope.ServiceProvider.GetRequiredService<ExceptionHookProbe>().HandledHandlerCalls.Should().Be(1);
    }

    [Fact(DisplayName = "The caller's own cancellation bypasses the exception handlers and reaches the caller")]
    public async Task Caller_cancellation_bypasses_the_exception_handlers()
    {
        await using var provider = BuildWithProbe();
        await using var scope = provider.CreateAsyncScope();
        using var caller = new CancellationTokenSource();

        var act = () => scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>()
            .Send(new CancelledDependencyCommand { Caller = caller }, caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        scope.ServiceProvider.GetRequiredService<ExceptionHookProbe>().HandledHandlerCalls.Should().Be(0);
    }

    [Fact(DisplayName = "A stream failing with a cancellation its consumer did not ask for reaches the exception handlers; the consumer's own does not")]
    public async Task Stream_foreign_cancellation_reaches_the_exception_handlers()
    {
        await using var provider = BuildWithProbe();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
        var probe = scope.ServiceProvider.GetRequiredService<ExceptionHookProbe>();

        var items = new List<int>();
        await foreach (var item in dispatcher.Stream(new CancelledDependencyStream(), TestContext.Current.CancellationToken))
            items.Add(item);
        items.Should().Equal(1, 99);
        probe.HandledHandlerCalls.Should().Be(1);

        using var caller = new CancellationTokenSource();
        var cancelled = async () =>
        {
            await foreach (var _ in dispatcher.Stream(new CancelledDependencyStream { Caller = caller }, caller.Token))
            {
            }
        };
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        probe.HandledHandlerCalls.Should().Be(1, "the consumer's own cancellation reached no handler");
    }

    private static ServiceProvider BuildWithProbe()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseValidation(false));
        services.AddSingleton<ExceptionHookProbe>();
        return services.BuildServiceProvider();
    }
}

public sealed class BaseActionProbe
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public void Record() => Interlocked.Increment(ref _calls);
}

public sealed class BaseHookExceptionAction(BaseActionProbe? probe = null) : IRequestExceptionAction<DerivedExceptionCommand, BaseHookException>
{
    public Task Execute(DerivedExceptionCommand request, BaseHookException exception, CancellationToken cancellationToken)
    {
        probe?.Record();
        return Task.CompletedTask;
    }
}
