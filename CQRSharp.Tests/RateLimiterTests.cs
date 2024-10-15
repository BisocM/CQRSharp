using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CQRSharp.Interfaces.Markers.Request;
using CQRSharp.RateLimiting.Behaviors;
using CQRSharp.RateLimiting.Enums;
using CQRSharp.RateLimiting.Exceptions;
using CQRSharp.RateLimiting.Handlers;
using CQRSharp.RateLimiting.Options;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CQRSharp.Tests
{
    public class RateLimiterTests
    {
        private readonly Mock<ILogger<RateLimitingBehavior<RequestBase, object>>> _behaviorLoggerMock;
        private readonly Mock<ILogger<RateLimiter>> _rateLimiterLoggerMock;
        private readonly Mock<IUserIdentifierFactory> _userIdentifierFactoryMock;
        private RateLimiterOptions _rateLimiterOptions;
        private RateLimiter _rateLimiter;
        private RateLimitingBehavior<RequestBase, object> _rateLimitingBehavior;

        public RateLimiterTests()
        {
            //Initialize mocks
            _behaviorLoggerMock = new Mock<ILogger<RateLimitingBehavior<RequestBase, object>>>();
            _rateLimiterLoggerMock = new Mock<ILogger<RateLimiter>>();
            _userIdentifierFactoryMock = new Mock<IUserIdentifierFactory>();

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
                _rateLimiter,
                _userIdentifierFactoryMock.Object);
        }

        [Fact]
        public async Task AllowRequest_WhenWithinLimit_ShouldAllow()
        {
            //Arrange
            var userId = "user1";
            var request = new TestCommand1();
            _userIdentifierFactoryMock.Setup(factory => factory.GetIdentifier(It.IsAny<RequestBase>())).Returns(userId);

            //Act & Assert
            await _rateLimitingBehavior.Handle(
                request, CancellationToken.None, _ => Task.FromResult<object>(null!));
        }

        [Fact]
        public async Task BlockRequest_WhenLimitExceeded_ShouldThrowRateLimitExceededException()
        {
            //Arrange
            var userId = "user2";
            var request = new TestCommand1();
            _userIdentifierFactoryMock.Setup(factory => factory.GetIdentifier(It.IsAny<RequestBase>())).Returns(userId);

            //Act: Consume all tokens within rate limit
            for (int i = 0; i < _rateLimiterOptions.MaxTokens; i++)
                await _rateLimitingBehavior.Handle(request, CancellationToken.None, _ => Task.FromResult<object>(null!));

            //Assert: Expect RateLimitExceededException on subsequent request
            await Assert.ThrowsAsync<RateLimitExceededException>(async () =>
                await _rateLimitingBehavior.Handle(request, CancellationToken.None, _ => Task.FromResult<object>(null!)));
        }

        [Fact]
        public async Task AllowRequest_AfterTokenRefill_ShouldAllow()
        {
            //Arrange
            var userId = "user3";
            var request = new TestCommand1();
            _userIdentifierFactoryMock.Setup(factory => factory.GetIdentifier(It.IsAny<RequestBase>())).Returns(userId);

            //Consume all tokens
            for (int i = 0; i < _rateLimiterOptions.MaxTokens; i++)
                await _rateLimitingBehavior.Handle(request, CancellationToken.None, _ => Task.FromResult<object>(null!));

            //Act: Wait for token refill and retry
            await Task.Delay(TimeSpan.FromSeconds(_rateLimiterOptions.MaxTokens / _rateLimiterOptions.ReplenishRatePerSecond + 1));

            //Assert: Should not throw, indicating the token was refilled
            await _rateLimitingBehavior.Handle(
                request, CancellationToken.None, _ => Task.FromResult<object>(null!));
        }

        [Fact]
        public async Task PerCommandScope_ShouldLimitIndependentlyPerCommand()
        {
            //Arrange
            var userId = "user4";
            var request1 = new TestCommand1();
            var request2 = new TestCommand2();
            _userIdentifierFactoryMock.Setup(factory => factory.GetIdentifier(It.IsAny<RequestBase>())).Returns(userId);

            //Act: Consume all tokens for request1
            for (var i = 0; i < _rateLimiterOptions.MaxTokens; i++)
                await _rateLimitingBehavior.Handle(request1, CancellationToken.None, _ => Task.FromResult<object>(null!));

            //Assert: Expect limit exceeded for request1
            await Assert.ThrowsAsync<RateLimitExceededException>(async () =>
                await _rateLimitingBehavior.Handle(request1, CancellationToken.None, _ => Task.FromResult<object>(null!)));

            //request2 should still be allowed as it has a separate rate limit in PerCommand scope
            await _rateLimitingBehavior.Handle(
                request2, CancellationToken.None, _ => Task.FromResult<object>(null!));
        }

        [Fact]
        public async Task GlobalScope_ShouldLimitAcrossAllCommands()
        {
            //Arrange
            _rateLimiterOptions.Scope = RateLimitScope.Global;
            _rateLimiter = new RateLimiter(_rateLimiterOptions, _rateLimiterLoggerMock.Object);
            _rateLimitingBehavior = new RateLimitingBehavior<RequestBase, object>(
                _behaviorLoggerMock.Object,
                _rateLimiter,
                _userIdentifierFactoryMock.Object);

            var userId = "user5";
            var request1 = new TestCommand1();
            var request2 = new TestCommand2();
            _userIdentifierFactoryMock.Setup(factory => factory.GetIdentifier(It.IsAny<RequestBase>())).Returns(userId);

            //Act: Consume all tokens using request1
            for (var i = 0; i < _rateLimiterOptions.MaxTokens; i++)
                await _rateLimitingBehavior.Handle(request1, CancellationToken.None, _ => Task.FromResult<object>(null!));

            //Assert: Expect limit exceeded for request2
            await Assert.ThrowsAsync<RateLimitExceededException>(async () =>
                await _rateLimitingBehavior.Handle(request2, CancellationToken.None, _ => Task.FromResult<object>(null!)));
        }

        [Fact]
        public async Task DifferentUsers_ShouldHaveIndependentLimits()
        {
            //Arrange
            var userId1 = "user6";
            var userId2 = "user7";
            var request = new TestCommand1();

            int callCount = 0;
            _userIdentifierFactoryMock.Setup(factory => factory.GetIdentifier(It.IsAny<RequestBase>()))
                .Returns(() =>
                {
                    if (callCount < _rateLimiterOptions.MaxTokens)
                    {
                        callCount++;
                        return userId1;
                    }
                    else
                    {
                        return userId2;
                    }
                });

            //Act: Consume all tokens for user1
            for (int i = 0; i < _rateLimiterOptions.MaxTokens; i++)
                await _rateLimitingBehavior.Handle(request, CancellationToken.None, _ => Task.FromResult<object>(null!));

            //Assert: user2 should still be allowed
            await _rateLimitingBehavior.Handle(
                request, CancellationToken.None, _ => Task.FromResult<object>(null!));

            //Optionally, verify that GetIdentifier was called the expected number of times
            _userIdentifierFactoryMock.Verify(factory => factory.GetIdentifier(It.IsAny<RequestBase>()), Times.Exactly(_rateLimiterOptions.MaxTokens + 1));
        }

        [Fact]
        public async Task NullUserIdentifier_ShouldThrowException()
        {
            //Arrange
            var request = new TestCommand1();
            _userIdentifierFactoryMock.Setup(factory => factory.GetIdentifier(It.IsAny<RequestBase>())).Returns((string?)null);

            //Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _rateLimitingBehavior.Handle(request, CancellationToken.None, _ => Task.FromResult<object>(null!)));
        }

        [Fact]
        public async Task HighThroughput_ShouldEnforceRateLimitsCorrectly()
        {
            //Arrange
            var userId = "user9";
            var request = new TestCommand1();
            _userIdentifierFactoryMock.Setup(factory => factory.GetIdentifier(It.IsAny<RequestBase>())).Returns(userId);

            var tasks = new Task[_rateLimiterOptions.MaxTokens * 2]; //Attempt twice the max tokens
            var exceptions = new ConcurrentQueue<Exception>();

            //Act
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    try
                    {
                        await _rateLimitingBehavior.Handle(request, CancellationToken.None, _ => Task.FromResult<object>(null!));
                    }
                    catch (Exception ex)
                    {
                        exceptions.Enqueue(ex);
                    }
                });
            }

            await Task.WhenAll(tasks);

            //Assert: The number of exceptions should be equal to tasks exceeding the max tokens
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

        //Define simple test command classes
        public class TestCommand1 : RequestBase { }
        public class TestCommand2 : RequestBase { }
    }
}