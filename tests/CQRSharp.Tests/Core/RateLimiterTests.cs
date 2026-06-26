using System.Collections.Concurrent;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Pipelines.Behaviors.RateLimiting;
using CQRSharp.Pipelines;
using CQRSharp.Pipelines.Extensions;
using CQRSharp.Pipelines.Options;
using CQRSharp.Tests.Shared;
using CQRSharp.Tests.Shared.CollisionsA;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit.Abstractions;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Contains unit tests for the <see cref="RateLimitingBehavior{TRequest,TResponse}" /> class.
/// </summary>
public class RateLimitingBehaviorTests
{
    private readonly RateLimitingBehavior<RequestBase<IRateLimitedContext>, object> _behavior;
    private readonly RateLimiterOptions _options;
    private readonly ITestOutputHelper _output;

    public RateLimitingBehaviorTests(ITestOutputHelper output)
    {
        _output = output;
        _options = new RateLimiterOptions
        {
            MaxTokens = 3,
            ReplenishRatePerSecond = 1,
            Scope = RateLimitScope.PerCommand
        };

        var rateLimiter = new RateLimiter(Options.Create(_options));
        _behavior = new RateLimitingBehavior<RequestBase<IRateLimitedContext>, object>(
            new Mock<ILogger<RateLimitingBehavior<RequestBase<IRateLimitedContext>, object>>>().Object,
            rateLimiter);
    }

    [Fact(DisplayName = "Under limit: allows single request without exception")]
    public async Task UnderLimit_AllowsSingleRequest()
    {
        // Arrange
        var ctx = new TestRateLimitedContext("r1", "u1");
        var req = new TestRateLimitedCommand { Context = ctx };

        // Act
        Func<Task> act = () => _behavior.Handle(req, _ => Task.FromResult<object>(null!), CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
        _output.WriteLine($"[PASS] User {ctx.UserId} request {ctx.RequestId} allowed.");
    }

    [Fact(DisplayName = "Over limit: blocks when tokens exhausted")]
    public async Task OverLimit_BlocksAfterMaxTokens()
    {
        // Arrange
        var ctx = new TestRateLimitedContext("r2", "u2");
        var req = new TestRateLimitedCommand { Context = ctx };
        for (var i = 0; i < _options.MaxTokens; i++) await _behavior.Handle(req, _ => Task.FromResult<object>(null!), CancellationToken.None);

        // Act
        Func<Task> act = () => _behavior.Handle(req, _ => Task.FromResult<object>(null!), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<RateLimitExceededException>()
            .WithMessage($"*{ctx.RequestId}*{ctx.UserId}*rate limit*");
        _output.WriteLine($"[PASS] After {_options.MaxTokens} calls, user {ctx.UserId} is blocked.");
    }

    [Fact(DisplayName = "After refill: tokens replenished over time")]
    public async Task AfterRefill_TokensReplenished()
    {
        // Arrange — a FakeTimeProvider-backed limiter so refill is driven by virtual time, not a real wall-clock wait.
        var time = new FakeTimeProvider();
        var limiter = new RateLimiter(Options.Create(_options), time);
        var behavior = new RateLimitingBehavior<RequestBase<IRateLimitedContext>, object>(
            new Mock<ILogger<RateLimitingBehavior<RequestBase<IRateLimitedContext>, object>>>().Object,
            limiter);

        var ctx = new TestRateLimitedContext("r3", "u3");
        var req = new TestRateLimitedCommand { Context = ctx };
        for (var i = 0; i < _options.MaxTokens; i++) await behavior.Handle(req, _ => Task.FromResult<object>(null!), CancellationToken.None);

        // Act — at 1 token/s, advancing MaxTokens(+1) seconds of virtual time refills the whole bucket.
        time.Advance(TimeSpan.FromSeconds(_options.MaxTokens / _options.ReplenishRatePerSecond + 1));
        Func<Task> act = () => behavior.Handle(req, _ => Task.FromResult<object>(null!), CancellationToken.None);

        // Assert
        await act.Should().NotThrowAsync();
        _output.WriteLine("[PASS] Tokens replenished for user u3 after virtual delay.");
    }

    [Fact(DisplayName = "Per-command scope: independent buckets per command type")]
    public async Task PerCommandScope_IndependentLimits()
    {
        // Arrange
        var uid = "u4";
        var cmd1 = new CollisionCommand { Context = new TestRateLimitedContext("r4a", uid) };
        var cmd2 = new Shared.CollisionsB.CollisionCommand { Context = new TestRateLimitedContext("r4b", uid) };
        for (var i = 0; i < _options.MaxTokens; i++) await _behavior.Handle(cmd1, _ => Task.FromResult<object>(null!), CancellationToken.None);

        // Act
        Func<Task> act2 = () => _behavior.Handle(cmd2, _ => Task.FromResult<object>(null!), CancellationToken.None);

        // Assert
        await act2.Should().NotThrowAsync();
        _output.WriteLine("[PASS] PerCommand: cmd1 exhaustion doesn't affect cmd2.");
    }

    [Fact(DisplayName = "Global scope: single bucket across command types")]
    public async Task GlobalScope_SharedLimitAcrossCommands()
    {
        // Arrange
        _options.Scope = RateLimitScope.Global;
        var globalLimiter = new RateLimiter(Options.Create(_options));
        var globalBehavior = new RateLimitingBehavior<RequestBase<IRateLimitedContext>, object>(
            new Mock<ILogger<RateLimitingBehavior<RequestBase<IRateLimitedContext>, object>>>().Object,
            globalLimiter);

        var uid = "u5";
        var c1 = new TestRateLimitedCommand { Context = new TestRateLimitedContext("r5a", uid) };
        var c2 = new OtherRateLimitedCommand { Context = new TestRateLimitedContext("r5b", uid) };
        for (var i = 0; i < _options.MaxTokens; i++) await globalBehavior.Handle(c1, _ => Task.FromResult<object>(null!), CancellationToken.None);

        // Act
        Func<Task> act2 = () => globalBehavior.Handle(c2, _ => Task.FromResult<object>(null!), CancellationToken.None);

        // Assert
        await act2.Should().ThrowAsync<RateLimitExceededException>();
        _output.WriteLine("[PASS] Global: exhaustion on c1 blocks c2 as well.");
    }

    [Fact(DisplayName = "Different users: independent buckets per user")]
    public async Task DifferentUsers_IndependentBuckets()
    {
        // Arrange
        var ctxA = new TestRateLimitedContext("r6a", "u6");
        var ctxB = new TestRateLimitedContext("r7a", "u7");
        for (var i = 0; i < _options.MaxTokens; i++)
            await _behavior.Handle(new TestRateLimitedCommand { Context = ctxA }, _ => Task.FromResult<object>(null!), CancellationToken.None);

        // Act
        Func<Task> actB = () => _behavior.Handle(new TestRateLimitedCommand { Context = ctxB }, _ => Task.FromResult<object>(null!), CancellationToken.None);

        // Assert
        await actB.Should().NotThrowAsync();
        _output.WriteLine("[PASS] Users u6 and u7 have separate buckets.");
    }

    [Fact(DisplayName = "No IRateLimitedContext: behavior is a no-op")]
    public async Task NoRateLimitedContext_NoOp()
    {
        // Arrange
        var limiter = new RateLimiter(Options.Create(_options));
        var behavior = new RateLimitingBehavior<TestCommand, object>(
            new Mock<ILogger<RateLimitingBehavior<TestCommand, object>>>().Object,
            limiter);

        var request = new TestCommand();

        // Act
        var result = await behavior.Handle(request, _ => Task.FromResult<object>(new object()), CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact(DisplayName = "Missing user: throws InvalidOperationException")]
    public async Task MissingUser_Throws()
    {
        // Arrange
        var ctx = new TestRateLimitedContext("r0", string.Empty);
        var req = new TestRateLimitedCommand { Context = ctx };

        // Act
        Func<Task> act = () => _behavior.Handle(req, _ => Task.FromResult<object>(null!), CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        _output.WriteLine("[PASS] Null user ID causes failure as expected.");
    }

    [Fact(DisplayName = "High throughput: concurrent enforcement")]
    public async Task HighThroughput_ConcurrencySafety()
    {
        // Arrange
        var ctx = new TestRateLimitedContext("r8", "u8");
        var req = new TestRateLimitedCommand { Context = ctx };
        var tasks = new Task[_options.MaxTokens * 2];
        var errors = new ConcurrentQueue<Exception>();

        // Act
        for (var i = 0; i < tasks.Length; i++)
            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    await _behavior.Handle(req, _ => Task.FromResult<object>(null!), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            });
        await Task.WhenAll(tasks);

        // Assert
        errors.Count.Should().Be(tasks.Length - _options.MaxTokens);
        _output.WriteLine($"[PASS] Concurrent calls enforced correctly: {errors.Count} failures.");
    }

    [Fact(DisplayName = "Invalid config: constructor throws ArgumentException")]
    public void InvalidConfig_Throws()
    {
        // Arrange
        var badOpts = new RateLimiterOptions { MaxTokens = -1, ReplenishRatePerSecond = 0 };

        // Act
        var act = () => new RateLimiter(Options.Create(badOpts));

        // Assert
        act.Should().Throw<ArgumentException>();
        _output.WriteLine("[PASS] Invalid options correctly cause constructor failure.");
    }

    [Fact(DisplayName = "DI registration: invalid options throw during resolution")]
    public void AddRateLimiting_InvalidOptions_ThrowsDuringResolution()
    {
        var services = new ServiceCollection();
        services.AddRateLimiting(options => options.MaxEntries = 0);

        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<RateLimiter>();
        act.Should().Throw<ArgumentException>()
            .WithMessage("*Rate limiting configuration is invalid*");
    }
}