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
        private readonly Mock<ILogger<RateLimitingBehavior<RequestBase, object>>> _loggerMock;
        private readonly Mock<IUserIdentifierFactory> _userIdentifierFactoryMock;
        private RateLimiter _rateLimiter;
        private RateLimitingBehavior<RequestBase, object> _rateLimitingBehavior;
        public RateLimiterTests()
        {
            // Initialize mocks
            _loggerMock = new Mock<ILogger<RateLimitingBehavior<RequestBase, object>>>();
            _userIdentifierFactoryMock = new Mock<IUserIdentifierFactory>();
            // Configure RateLimiterConfig
            var rateLimiterConfig = new RateLimiterOptions
            {
                Scope = RateLimitScope.PerCommand
            };

            _rateLimiter = new RateLimiter(rateLimiterConfig, new Mock<ILogger<RateLimiter>>().Object);
            // Instantiate RateLimitingBehavior with mocks
            _rateLimitingBehavior = new RateLimitingBehavior<RequestBase, object>(
                _loggerMock.Object,
                _rateLimiter,
                _userIdentifierFactoryMock.Object);
        }
        [Fact]
        public async Task AllowRequest_WhenWithinLimit_ShouldAllow()
        {
            // Arrange
            var userId = "user1";
            var request = new Mock<RequestBase>();
            _userIdentifierFactoryMock.Setup(factory => factory.GetIdentifier(It.IsAny<RequestBase>())).Returns(userId);
            // Act
            Func<Task> action = async () => await _rateLimitingBehavior.Handle(
                request.Object, CancellationToken.None, _ => Task.FromResult<object>(new object()));
            // Assert: Expect no exceptions and allow request within rate limit
            await action.Should().NotThrowAsync();
        }
        [Fact]
        public async Task BlockRequest_WhenLimitExceeded_ShouldThrowRateLimitExceededException()
        {
            // Arrange
            var userId = "user2";
            var request = new Mock<RequestBase>();
            _userIdentifierFactoryMock.Setup(factory => factory.GetIdentifier(It.IsAny<RequestBase>())).Returns(userId);
            
            // Act: Consume all tokens within rate limit
            for (int i = 0; i < 3; i++)
                await _rateLimitingBehavior.Handle(request.Object, CancellationToken.None, _ => Task.FromResult<object>(new object()));
            
            //Assert: Expect RateLimitExceededException on subsequent request
            await Assert.ThrowsAsync<RateLimitExceededException>(async () =>
                await _rateLimitingBehavior.Handle(request.Object, CancellationToken.None, _ => Task.FromResult<object>(new object())));
        }
        [Fact]
        public async Task AllowRequest_AfterTokenRefill_ShouldAllow()
        {
            // Arrange
            var userId = "user3";
            var request = new Mock<RequestBase>();
            _userIdentifierFactoryMock.Setup(factory => factory.GetIdentifier(It.IsAny<RequestBase>())).Returns(userId);
            // Consume all tokens
            for (int i = 0; i < 3; i++)
                await _rateLimitingBehavior.Handle(request.Object, CancellationToken.None, _ => Task.FromResult<object>(new object()));
            // Act: Wait for token refill and retry
            await Task.Delay(TimeSpan.FromSeconds(2)); // Wait enough time for at least one token refill
            Func<Task> action = async () => await _rateLimitingBehavior.Handle(
                request.Object, CancellationToken.None, _ => Task.FromResult<object>(new object()));
            // Assert: Should not throw, indicating the token was refilled
            await action.Should().NotThrowAsync();
        }
        [Fact]
        public async Task PerCommandScope_ShouldLimitIndependentlyPerCommand()
        {
            // Arrange
            var userId = "user4";
            var command1 = new Mock<RequestBase>();
            var command2 = new Mock<RequestBase>();
            _userIdentifierFactoryMock.Setup(factory => factory.GetIdentifier(It.IsAny<RequestBase>())).Returns(userId);
            // Act & Assert
            // First command should consume tokens
            for (var i = 0; i < 3; i++)
                await _rateLimitingBehavior.Handle(command1.Object, CancellationToken.None, _ => Task.FromResult<object>(new object()));
            // Expect limit exceeded for command1 after tokens are consumed
            await Assert.ThrowsAsync<RateLimitExceededException>(async () =>
                await _rateLimitingBehavior.Handle(command1.Object, CancellationToken.None, _ => Task.FromResult<object>(new object())));
            // Command2 should still be allowed as it has a separate rate limit in PerCommand scope
            Func<Task> action = async () => await _rateLimitingBehavior.Handle(
                command2.Object, CancellationToken.None, _ => Task.FromResult<object>(new object()));
            await action.Should().NotThrowAsync();
        }
    }
}