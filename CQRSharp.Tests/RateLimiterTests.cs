using System.Collections.Concurrent;
using CQRSharp.Core.Pipelines.Types.RateLimiting;
using CQRSharp.Core.Pipelines.Types.RateLimiting.Context;
using CQRSharp.Shared.Data.Interfaces.Markers.Request;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit.Abstractions;

namespace CQRSharp.Tests
{
    /// <summary>
    /// Unit tests for RateLimitingBehavior covering all core scenarios:
    /// - under/over limit
    /// - token refill
    /// - per-command vs global scopes
    /// - multi-user isolation
    /// - null user handling
    /// - concurrency safety
    /// - invalid configuration
    /// </summary>
    public class RateLimitingBehaviorTests
    {
        private readonly ITestOutputHelper _output;
        private readonly RateLimiterOptions _options;
        private readonly RateLimitingBehavior<RequestBase<IRateLimitedContext>, object> _behavior;

        public RateLimitingBehaviorTests(ITestOutputHelper output)
        {
            _output = output;
            _options = new RateLimiterOptions
            {
                MaxTokens = 3,
                ReplenishRatePerSecond = 1,
                Scope = RateLimitScope.PerCommand
            };

            //Instantiate RateLimiter and behavior under PerCommand scope using IOptions
            var rateLimiter = new RateLimiter(Options.Create(_options));
            _behavior = new RateLimitingBehavior<RequestBase<IRateLimitedContext>, object>(
                new Mock<ILogger<RateLimitingBehavior<RequestBase<IRateLimitedContext>, object>>>().Object,
                rateLimiter);
        }

        [Fact(DisplayName = "Under limit: allows single request without exception")]
        public async Task UnderLimit_AllowsSingleRequest()
        {
            var ctx = new TestContext("r1", "u1");
            var req = new TestCmd { Context = ctx };

            Func<Task> act = () => _behavior.Handle(req, _ => Task.FromResult<object>(null!), CancellationToken.None);
            await act.Should().NotThrowAsync();
            _output.WriteLine($"[PASS] User {ctx.UserId} request {ctx.RequestId} allowed (tokens={_options.MaxTokens}).");
        }

        [Fact(DisplayName = "Over limit: blocks when tokens exhausted")]
        public async Task OverLimit_BlocksAfterMaxTokens()
        {
            var ctx = new TestContext("r2", "u2");
            var req = new TestCmd { Context = ctx };
            for (int i = 0; i < _options.MaxTokens; i++)
                await _behavior.Handle(req, _ => Task.FromResult<object>(null!), CancellationToken.None);

            Func<Task> act = () => _behavior.Handle(req, _ => Task.FromResult<object>(null!), CancellationToken.None);
            await act.Should().ThrowAsync<RateLimitExceededException>()
                     .WithMessage($"*{ctx.RequestId}*{ctx.UserId}*rate limit*");

            _output.WriteLine($"[PASS] After {_options.MaxTokens} calls, user {ctx.UserId} is blocked.");
        }

        [Fact(DisplayName = "After refill: tokens replenished over time")]
        public async Task AfterRefill_TokensReplenished()
        {
            var ctx = new TestContext("r3", "u3");
            var req = new TestCmd { Context = ctx };
            for (int i = 0; i < _options.MaxTokens; i++)
                await _behavior.Handle(req, _ => Task.FromResult<object>(null!), CancellationToken.None);

            //Wait long enough to refill all tokens
            await Task.Delay(TimeSpan.FromSeconds(_options.MaxTokens / _options.ReplenishRatePerSecond + 1));

            //Should succeed after refill
            Func<Task> act = () => _behavior.Handle(req, _ => Task.FromResult<object>(null!), CancellationToken.None);
            await act.Should().NotThrowAsync();

            _output.WriteLine("[PASS] Tokens replenished for user u3 after delay.");
        }

        [Fact(DisplayName = "Per-command scope: independent buckets per command type")]
        public async Task PerCommandScope_IndependentLimits()
        {
            var uid = "u4";
            var cmd1 = new TestCmd { Context = new TestContext("r4a", uid) };
            var cmd2 = new OtherCmd { Context = new TestContext("r4b", uid) };

            //Exhaust tokens on TestCmd
            for (int i = 0; i < _options.MaxTokens; i++)
                await _behavior.Handle(cmd1, _ => Task.FromResult<object>(null!), CancellationToken.None);

            //OtherCmd should still be allowed
            Func<Task> act2 = () => _behavior.Handle(cmd2, _ => Task.FromResult<object>(null!), CancellationToken.None);
            await act2.Should().NotThrowAsync();

            _output.WriteLine("[PASS] PerCommand: cmd1 exhaustion doesn't affect cmd2.");
        }

        [Fact(DisplayName = "Global scope: single bucket across command types")]
        public async Task GlobalScope_SharedLimitAcrossCommands()
        {
            //Switch to global
            _options.Scope = RateLimitScope.Global;
            var globalLimiter = new RateLimiter(Options.Create(_options));
            var globalBehavior = new RateLimitingBehavior<RequestBase<IRateLimitedContext>, object>(
                new Mock<ILogger<RateLimitingBehavior<RequestBase<IRateLimitedContext>, object>>>().Object,
                globalLimiter);

            var uid = "u5";
            var c1 = new TestCmd { Context = new TestContext("r5a", uid) };
            var c2 = new OtherCmd { Context = new TestContext("r5b", uid) };

            //Exhaust via c1
            for (int i = 0; i < _options.MaxTokens; i++)
                await globalBehavior.Handle(c1, _ => Task.FromResult<object>(null!), CancellationToken.None);

            //c2 should now be blocked
            Func<Task> act2 = () => globalBehavior.Handle(c2, _ => Task.FromResult<object>(null!), CancellationToken.None);
            await act2.Should().ThrowAsync<RateLimitExceededException>();

            _output.WriteLine("[PASS] Global: exhaustion on c1 blocks c2 as well.");
        }

        [Fact(DisplayName = "Different users: independent buckets per user")]
        public async Task DifferentUsers_IndependentBuckets()
        {
            var ctxA = new TestContext("r6a", "u6");
            var ctxB = new TestContext("r7a", "u7");

            //Exhaust u6
            for (int i = 0; i < _options.MaxTokens; i++)
                await _behavior.Handle(new TestCmd { Context = ctxA }, _ => Task.FromResult<object>(null!), CancellationToken.None);

            //u7 should still succeed
            Func<Task> actB = () => _behavior.Handle(new TestCmd { Context = ctxB }, _ => Task.FromResult<object>(null!), CancellationToken.None);
            await actB.Should().NotThrowAsync();

            _output.WriteLine("[PASS] Users u6 and u7 have separate buckets.");
        }

        [Fact(DisplayName = "Null user: throws InvalidOperationException")]
        public async Task NullUser_Throws()
        {
            var ctx = new TestContext("r0", null);
            var req = new TestCmd { Context = ctx };

            Func<Task> act = () => _behavior.Handle(req, _ => Task.FromResult<object>(null!), CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();

            _output.WriteLine("[PASS] Null user ID causes failure as expected.");
        }

        [Fact(DisplayName = "High throughput: concurrent enforcement")]
        public async Task HighThroughput_ConcurrencySafety()
        {
            var ctx = new TestContext("r8", "u8");
            var req = new TestCmd { Context = ctx };
            var tasks = new Task[_options.MaxTokens * 2];
            var errors = new ConcurrentQueue<Exception>();

            for (var i = 0; i < tasks.Length; i++)
                tasks[i] = Task.Run(async () =>
                {
                    try { await _behavior.Handle(req, _ => Task.FromResult<object>(null!), CancellationToken.None); }
                    catch (Exception ex) { errors.Enqueue(ex); }
                });

            await Task.WhenAll(tasks);
            errors.Count.Should().Be(tasks.Length - _options.MaxTokens);

            _output.WriteLine($"[PASS] Concurrent calls enforced correctly: {errors.Count} failures.");
        }

        [Fact(DisplayName = "Invalid config: constructor throws ArgumentException")]
        public void InvalidConfig_Throws()
        {
            var badOpts = new RateLimiterOptions { MaxTokens = -1, ReplenishRatePerSecond = 0 };
            var act = () => { new RateLimiter(Options.Create(badOpts)); };
            act.Should().Throw<ArgumentException>();

            _output.WriteLine("[PASS] Invalid options correctly cause constructor failure.");
        }

        private class TestCmd : RequestBase<IRateLimitedContext> { }
        private class OtherCmd : RequestBase<IRateLimitedContext> { }
    }

    /// <summary>
    /// Simple context implementation for unit tests.
    /// </summary>
    public class TestContext(object requestId, object userId) : IRateLimitedContext
    {
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public object RequestId { get; set; } = requestId;
        public object UserId { get; set; } = userId;
    }
}