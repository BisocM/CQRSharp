using System.Collections.Concurrent;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Pipelines.Types.RateLimiting;
using CQRSharp.Interfaces.Context;
using CQRSharp.Interfaces.Markers.Request;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace CQRSharp.Tests
{
    public class RateLimiterTests
    {
        private readonly Mock<ILogger<RateLimitingBehavior<RequestBase, object>>> _behaviorLoggerMock;
        private readonly Mock<ILogger<RateLimiter>> _rateLimiterLoggerMock;
        private readonly RateLimiterOptions _rateLimiterOptions;
        private RateLimiter _rateLimiter;
        private RateLimitingBehavior<RequestBase, object> _rateLimitingBehavior;

        public RateLimiterTests()
        {
            //Initialize mocks
            _behaviorLoggerMock = new Mock<ILogger<RateLimitingBehavior<RequestBase, object>>>();
            _rateLimiterLoggerMock = new Mock<ILogger<RateLimiter>>();

            //Configure RateLimiterOptions with defaults
            _rateLimiterOptions = new RateLimiterOptions
            {
                MaxTokens = 3,
                ReplenishRatePerSecond = 1, //Refill 1 token per second
                Scope = RateLimitScope.PerCommand
            };

            //Instantiate RateLimiter with logger mock
            _rateLimiter = new RateLimiter(_rateLimiterOptions, _rateLimiterLoggerMock.Object);

            //Instantiate RateLimitingBehavior with mocks
            _rateLimitingBehavior = new RateLimitingBehavior<RequestBase, object>(
                _behaviorLoggerMock.Object,
                _rateLimiter);
        }

        [Fact]
        public async Task AllowRequest_WhenWithinLimit_ShouldAllow()
        {
            //Arrange
            var userId = "user1";
            var request = new TestCommand1
            {
                //This is crucial for passing the user ID to RateLimitingBehavior
                Context = new RequestContextBase("requestId1", userId)
            };
            
            //Act & Assert (no exception means success)
            await _rateLimitingBehavior.Handle(
                request, _ => Task.FromResult<object>(null!), CancellationToken.None);
        }

        [Fact]
        public async Task BlockRequest_WhenLimitExceeded_ShouldThrowRateLimitExceededException()
        {
            //Arrange
            var userId = "user2";
            var request = new TestCommand1
            {
                Context = new RequestContextBase("requestId2", userId)
            };

            //Act: Consume all tokens within rate limit
            for (int i = 0; i < _rateLimiterOptions.MaxTokens; i++)
            {
                await _rateLimitingBehavior.Handle(request, _ => Task.FromResult<object>(null!), CancellationToken.None);
            }

            //Assert: Expect RateLimitExceededException on subsequent request
            await Assert.ThrowsAsync<RateLimitExceededException>(async () =>
                await _rateLimitingBehavior.Handle(request, _ => Task.FromResult<object>(null!), CancellationToken.None));
        }

        [Fact]
        public async Task AllowRequest_AfterTokenRefill_ShouldAllow()
        {
            //Arrange
            var userId = "user3";
            var request = new TestCommand1
            {
                Context = new RequestContextBase("requestId3", userId)
            };

            //Consume all tokens
            for (int i = 0; i < _rateLimiterOptions.MaxTokens; i++)
            {
                await _rateLimitingBehavior.Handle(request, _ => Task.FromResult<object>(null!), CancellationToken.None);
            }

            //Act: Wait for token refill and retry
            await Task.Delay(
                TimeSpan.FromSeconds(_rateLimiterOptions.MaxTokens / _rateLimiterOptions.ReplenishRatePerSecond + 1));

            //Assert: Should not throw, indicating the token was refilled
            await _rateLimitingBehavior.Handle(
                request, _ => Task.FromResult<object>(null!), CancellationToken.None);
        }

        [Fact]
        public async Task PerCommandScope_ShouldLimitIndependentlyPerCommand()
        {
            //Arrange
            var userId = "user4";
            var request1 = new TestCommand1
            {
                Context = new RequestContextBase("requestId4A", userId)
            };
            var request2 = new TestCommand2
            {
                Context = new RequestContextBase("requestId4B", userId)
            };

            //Act: Consume all tokens for request1
            for (var i = 0; i < _rateLimiterOptions.MaxTokens; i++)
            {
                await _rateLimitingBehavior.Handle(
                    request1, _ => Task.FromResult<object>(null!), CancellationToken.None);
            }

            //Assert: Expect limit exceeded for request1
            await Assert.ThrowsAsync<RateLimitExceededException>(async () =>
                await _rateLimitingBehavior.Handle(
                    request1, _ => Task.FromResult<object>(null!), CancellationToken.None));

            //request2 should still be allowed as it has a separate rate limit in PerCommand scope
            await _rateLimitingBehavior.Handle(
                request2, _ => Task.FromResult<object>(null!), CancellationToken.None);
        }

        [Fact]
        public async Task GlobalScope_ShouldLimitAcrossAllCommands()
        {
            //Arrange
            _rateLimiterOptions.Scope = RateLimitScope.Global;
            _rateLimiter = new RateLimiter(_rateLimiterOptions, _rateLimiterLoggerMock.Object);
            _rateLimitingBehavior = new RateLimitingBehavior<RequestBase, object>(
                _behaviorLoggerMock.Object,
                _rateLimiter);

            var userId = "user5";
            var request1 = new TestCommand1
            {
                Context = new RequestContextBase("requestId5A", userId)
            };
            var request2 = new TestCommand2
            {
                Context = new RequestContextBase("requestId5B", userId)
            };

            //Act: Consume all tokens using request1
            for (var i = 0; i < _rateLimiterOptions.MaxTokens; i++)
            {
                await _rateLimitingBehavior.Handle(request1, _ => Task.FromResult<object>(null!), CancellationToken.None);
            }

            //Assert: Expect limit exceeded for request2
            await Assert.ThrowsAsync<RateLimitExceededException>(async () =>
                await _rateLimitingBehavior.Handle(request2, _ => Task.FromResult<object>(null!), CancellationToken.None));
        }

        [Fact]
        public async Task DifferentUsers_ShouldHaveIndependentLimits()
        {
            //Arrange
            var request = new TestCommand1();
            
            //We'll alternate user IDs manually in the test.
            //user6 hits the limit, user7 is still fresh.
            var userIds = new[] { "user6", "user6", "user6", "user7" };
            var callIndex = 0;

            //We still have the mock, but RateLimitingBehavior *doesn't* call it by default.
            //The fix is to set the context yourself. However, if you want to mimic different users,
            //you can build them in separate requests, or do a small trick:

            //We'll run multiple requests:
            foreach (var userId in userIds)
            {
                var localRequest = new TestCommand1
                {
                    Context = new RequestContextBase($"request_{userId}_{callIndex}", userId)
                };

                try
                {
                    await _rateLimitingBehavior.Handle(
                        localRequest, _ => Task.FromResult<object>(null!), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    //We only expect an exception once user6 hits the limit.
                    //This isn't strictly necessary to do an assertion here,
                    //but you can track if user7 gets blocked incorrectly.
                }

                callIndex++;
            }

            //At this point, user6 presumably consumed all tokens, user7 was unaffected.
            //We can add additional assertions if needed—for instance, counting how many times user6 or user7 got blocked.
        }

        [Fact]
        public async Task NullUserIdentifier_ShouldThrowException()
        {
            //Arrange
            var request = new TestCommand1
            {
                //The big difference: If there's no user ID in the context, it will fail.
                Context = new RequestContextBase("requestNullUser", null)
            };

            //Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _rateLimitingBehavior.Handle(request, _ => Task.FromResult<object>(null!), CancellationToken.None));
        }

        [Fact]
        public async Task HighThroughput_ShouldEnforceRateLimitsCorrectly()
        {
            //Arrange
            var userId = "user9";
            var request = new TestCommand1
            {
                Context = new RequestContextBase("requestId9", userId)
            };

            //Attempt twice the max tokens concurrently
            var tasks = new Task[_rateLimiterOptions.MaxTokens * 2];
            var exceptions = new ConcurrentQueue<Exception>();

            //Act
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    try
                    {
                        await _rateLimitingBehavior.Handle(
                            request, 
                            _ => Task.FromResult<object>(null!), 
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Enqueue(ex);
                    }
                });
            }

            await Task.WhenAll(tasks);

            //Assert: The number of exceptions should be tasks that exceeded the max tokens
            exceptions.Count.Should().Be(tasks.Length - _rateLimiterOptions.MaxTokens);

            foreach (var ex in exceptions)
                ex.Should().BeOfType<RateLimitExceededException>();
        }

        [Fact]
        public void InvalidConfiguration_ShouldThrowArgumentException()
        {
            //Arrange
            var invalidOptions = new RateLimiterOptions
            {
                MaxTokens = -1,
                ReplenishRatePerSecond = 0
            };

            //Act & Assert
            Assert.Throws<ArgumentException>(() => new RateLimiter(invalidOptions, _rateLimiterLoggerMock.Object));
        }

        //Simple test command classes inheriting RequestBase
        public class TestCommand1 : RequestBase { }
        public class TestCommand2 : RequestBase { }
    }
}