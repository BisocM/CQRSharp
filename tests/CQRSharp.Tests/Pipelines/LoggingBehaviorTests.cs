using CQRSharp.Pipelines.Behaviors.Logging;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Tests for <see cref="LoggingBehavior{TRequest, TResult}" /> and
///     <see cref="StreamLoggingBehavior{TRequest, TItem}" />: the behaviors are transparent (they invoke the next
///     delegate and return its result/items unchanged) and they log around the request — on the success path and when
///     the next delegate throws.
/// </summary>
public class LoggingBehaviorTests
{
    /// <summary>
    ///     A minimal in-memory <see cref="ILogger{T}" /> that captures every log entry. Verifying log output through a
    ///     real-ish logger is more robust than mocking <c>ILogger.Log&lt;TState&gt;</c> directly, because the
    ///     <c>LogInformation</c>/<c>LogError</c> extension methods funnel through the generic <c>Log</c> method.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    // -------------------------------------------------------------------------------------------------------------
    // LoggingBehavior<TRequest, TResult>
    // -------------------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "Logging behavior invokes the next delegate and returns its result unchanged")]
    public async Task Logging_InvokesNext_AndReturnsResultUnchanged()
    {
        var logger = new CapturingLogger<LoggingBehavior<TestCommand, string>>();
        var behavior = new LoggingBehavior<TestCommand, string>(logger);
        var expected = "the-result";
        var called = 0;

        var result = await behavior.Handle(
            new TestCommand(),
            _ =>
            {
                called++;
                return Task.FromResult(expected);
            },
            CancellationToken.None);

        called.Should().Be(1, "the behavior must delegate to the inner handler exactly once");
        result.Should().BeSameAs(expected, "the result must be returned unchanged");
    }

    [Fact(DisplayName = "Logging behavior logs before and after a successful request")]
    public async Task Logging_OnSuccess_LogsAroundRequest()
    {
        var logger = new CapturingLogger<LoggingBehavior<TestCommand, string>>();
        var behavior = new LoggingBehavior<TestCommand, string>(logger);

        await behavior.Handle(
            new TestCommand(),
            _ => Task.FromResult("ok"),
            CancellationToken.None);

        logger.Entries.Should().HaveCount(2, "success logs once on entry and once on completion");
        logger.Entries.Should().OnlyContain(e => e.Level == LogLevel.Information);
        logger.Entries[0].Message.Should().Contain("Handling").And.Contain(nameof(TestCommand));
        logger.Entries[1].Message.Should().Contain("Handled").And.Contain(nameof(TestCommand));
        logger.Entries.Should().NotContain(e => e.Exception != null);
    }

    [Fact(DisplayName = "Logging behavior propagates the next delegate's exception and logs an error")]
    public async Task Logging_OnFailure_LogsError_AndPropagates()
    {
        var logger = new CapturingLogger<LoggingBehavior<TestCommand, string>>();
        var behavior = new LoggingBehavior<TestCommand, string>(logger);
        var boom = new InvalidOperationException("boom");

        Func<Task> act = () => behavior.Handle(
            new TestCommand(),
            _ => throw boom,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");

        // Entry log on start, then an error log on failure (no completion log).
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains("Handling"));
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error &&
            e.Message.Contains("failed") &&
            ReferenceEquals(e.Exception, boom));
        logger.Entries.Should().NotContain(e => e.Message.Contains("Handled "),
            "a failed request must not emit the success-completion log");
    }

    // -------------------------------------------------------------------------------------------------------------
    // StreamLoggingBehavior<TRequest, TItem>
    // -------------------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "Stream logging behavior passes every item through unchanged")]
    public async Task StreamLogging_PassesItemsThrough()
    {
        var logger = new CapturingLogger<StreamLoggingBehavior<TestStreamRequest, int>>();
        var behavior = new StreamLoggingBehavior<TestStreamRequest, int>(logger);
        var source = new[] { 10, 20, 30 };
        var nextCalled = 0;

        var collected = new List<int>();
        await foreach (var item in behavior.Handle(
                           new TestStreamRequest(0),
                           _ =>
                           {
                               nextCalled++;
                               return ToAsync(source);
                           },
                           CancellationToken.None))
        {
            collected.Add(item);
        }

        nextCalled.Should().Be(1, "the inner stream factory is invoked exactly once during enumeration");
        collected.Should().Equal(source, "items must flow through the behavior unchanged and in order");
    }

    [Fact(DisplayName = "Stream logging behavior logs once on start and once on completion with the item count")]
    public async Task StreamLogging_OnSuccess_LogsAroundEnumeration()
    {
        var logger = new CapturingLogger<StreamLoggingBehavior<TestStreamRequest, int>>();
        var behavior = new StreamLoggingBehavior<TestStreamRequest, int>(logger);

        var enumerated = behavior.Handle(
            new TestStreamRequest(0),
            _ => ToAsync([1, 2]),
            CancellationToken.None);

        // The start log only happens once enumeration begins (it lives inside the async iterator).
        logger.Entries.Should().BeEmpty("nothing is logged until the stream is enumerated");

        await foreach (var _ in enumerated) { }

        logger.Entries.Should().HaveCount(2, "logs once at start of enumeration and once after completion");
        logger.Entries.Should().OnlyContain(e => e.Level == LogLevel.Information);
        logger.Entries[0].Message.Should().Contain("Streaming").And.Contain(nameof(TestStreamRequest));
        logger.Entries[1].Message.Should().Contain("Streamed").And.Contain(nameof(TestStreamRequest));
    }

    [Fact(DisplayName = "Stream logging behavior logs an error and propagates when the inner stream throws mid-enumeration")]
    public async Task StreamLogging_OnFailure_LogsError_AndPropagates()
    {
        var logger = new CapturingLogger<StreamLoggingBehavior<TestStreamRequest, int>>();
        var behavior = new StreamLoggingBehavior<TestStreamRequest, int>(logger);
        var boom = new InvalidOperationException("stream-boom");

        Func<Task> act = async () =>
        {
            await foreach (var _ in behavior.Handle(
                               new TestStreamRequest(0),
                               _ => ThrowAfter(boom),
                               CancellationToken.None))
            {
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("stream-boom");

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains("Streaming"));
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error &&
            e.Message.Contains("failed") &&
            ReferenceEquals(e.Exception, boom));
        logger.Entries.Should().NotContain(e => e.Message.Contains("Streamed "),
            "a failed stream must not emit the success-completion log");
    }

    // -------------------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------------------

    private static async IAsyncEnumerable<int> ToAsync(IEnumerable<int> items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    private static async IAsyncEnumerable<int> ThrowAfter(Exception ex)
    {
        await Task.Yield();
        yield return 1;
        throw ex;
    }
}
