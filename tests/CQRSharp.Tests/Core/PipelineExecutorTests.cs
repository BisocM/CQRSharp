// CQRSharp.Tests/Core/PipelineExecutorTests.cs

using CQRSharp.Abstractions.Data.Interfaces.Context;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Data.Models.Requests;
using CQRSharp.Core.Caching.Contexts;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Notifications.Types;
using CQRSharp.Core.Pipelines;
using CQRSharp.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Contains unit tests for the <see cref="PipelineExecutor" /> class.
/// </summary>
public class PipelineExecutorTests
{
    private readonly PipelineExecutor _executor;
    private readonly Mock<IHandlerRegistry> _mockHandlerRegistry;
    private readonly Mock<INotificationDispatcher> _mockNotificationDispatcher;
    private readonly Mock<IRequestRegistry> _mockRequestRegistry;
    private readonly Mock<IServiceProvider> _mockScopedProvider;

    public PipelineExecutorTests()
    {
        var mockRootProvider = new Mock<IServiceProvider>();
        var mockScope = new Mock<IServiceScope>();
        _mockScopedProvider = new Mock<IServiceProvider>();
        _mockRequestRegistry = new Mock<IRequestRegistry>();
        _mockHandlerRegistry = new Mock<IHandlerRegistry>();
        var mockContextFactoryRegistry = new Mock<IContextFactoryRegistry>();
        _mockNotificationDispatcher = new Mock<INotificationDispatcher>();

        _executor = new PipelineExecutor(
            mockRootProvider.Object,
            _mockRequestRegistry.Object,
            _mockHandlerRegistry.Object,
            mockContextFactoryRegistry.Object
        );

        mockScope.Setup(s => s.ServiceProvider).Returns(_mockScopedProvider.Object);
        mockRootProvider.Setup(p => p.GetService(typeof(IServiceScopeFactory))).Returns(mockScope.Object);
        _mockScopedProvider.Setup(p => p.GetService(typeof(INotificationDispatcher))).Returns(_mockNotificationDispatcher.Object);

        var mockContextFactory = new Mock<IInternalRequestContextFactory>();
        mockContextFactory.Setup(f => f.CreateContext(It.IsAny<IRequest>())).Returns(new RequestContextBase());
        mockContextFactoryRegistry.Setup(r => r.TryGetFactory(It.IsAny<Type>(), It.IsAny<IServiceProvider>())).Returns(mockContextFactory.Object);
    }

    private void SetupHandlerResolution<THandler>(THandler handler) where THandler : class
    {
        _mockScopedProvider.Setup(p => p.GetService(typeof(THandler))).Returns(handler);
    }

    [Fact]
    public async Task ExecuteCommandAsync_InvokesHandlerAndPublishesNotifications()
    {
        // Arrange
        var command = new TestCommand();
        var handler = new TestCommandHandler();
        var requestType = typeof(TestCommand);
        var handlerType = typeof(TestCommandHandler);
        RequestMetadata metadata = new(requestType, handlerType, [], [], [], [], null, typeof(RequestContextBase));

        _mockRequestRegistry.Setup(r => r.TryGetRequestMetadata(requestType, out metadata!)).Returns(true);
        _mockHandlerRegistry.Setup(r => r.TryGetHandlerDelegate(requestType, out It.Ref<HandlerInvokerDelegate>.IsAny))
            .Returns((Type _, out HandlerInvokerDelegate del) =>
            {
                del = (h, r, c) => ((TestCommandHandler)h).Handle((TestCommand)r, c).ContinueWith(t => (object)t.Result, TaskScheduler.Default);
                return true;
            });

        SetupHandlerResolution(handler);

        // Act
        var result = await _executor.ExecuteCommandAsync(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        _mockNotificationDispatcher.Verify(d => d.Publish(It.IsAny<CommandInitiatedNotification>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockNotificationDispatcher.Verify(d => d.Publish(It.IsAny<CommandCompletedNotification>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_WithPreAndPostHandlerAttributes_InvokesAttributesInOrder()
    {
        // Arrange
        var command = new TestCommand();
        var handler = new TestCommandHandler();
        var requestType = typeof(TestCommand);
        var handlerType = typeof(TestCommandHandler);
        var callOrder = new List<string>();

        var mockPreHandler1 = new Mock<TestPreHandlerAttribute>(1);
        var mockPreHandler2 = new Mock<TestPreHandlerAttribute>(2);
        var mockPostHandler1 = new Mock<TestPostHandlerAttribute>(1);
        var mockPostHandler2 = new Mock<TestPostHandlerAttribute>(2);

        mockPreHandler1.Setup(p => p.OnBeforeHandle(It.IsAny<IRequest>(), It.IsAny<IServiceProvider>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("pre1")).Returns(Task.CompletedTask);
        mockPreHandler2.Setup(p => p.OnBeforeHandle(It.IsAny<IRequest>(), It.IsAny<IServiceProvider>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("pre2")).Returns(Task.CompletedTask);
        mockPostHandler1.Setup(p => p.OnAfterHandle(It.IsAny<IRequest>(), It.IsAny<IServiceProvider>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("post1")).Returns(Task.CompletedTask);
        mockPostHandler2.Setup(p => p.OnAfterHandle(It.IsAny<IRequest>(), It.IsAny<IServiceProvider>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("post2")).Returns(Task.CompletedTask);

        RequestMetadata metadata = new(requestType, handlerType,
            [mockPreHandler1.Object, mockPreHandler2.Object],
            [mockPostHandler1.Object, mockPostHandler2.Object],
            [], [], null, typeof(RequestContextBase));

        _mockRequestRegistry.Setup(r => r.TryGetRequestMetadata(requestType, out metadata!)).Returns(true);
        _mockHandlerRegistry.Setup(r => r.TryGetHandlerDelegate(requestType, out It.Ref<HandlerInvokerDelegate>.IsAny))
            .Returns((Type _, out HandlerInvokerDelegate del) =>
            {
                del = (h, r, c) =>
                {
                    callOrder.Add("handle");
                    return ((TestCommandHandler)h).Handle((TestCommand)r, c).ContinueWith(t => (object)t.Result, TaskScheduler.Default);
                };
                return true;
            });

        SetupHandlerResolution(handler);

        // Act
        await _executor.ExecuteCommandAsync(command, CancellationToken.None);

        // Assert
        Assert.Equal(["pre1", "pre2", "handle", "post1", "post2"], callOrder);
    }

    [Fact]
    public async Task Execute_WhenMetadataIsMissing_ThrowsInvalidOperationException()
    {
        // Arrange
        var command = new TestCommand();
        RequestMetadata? metadata = null;
        _mockRequestRegistry.Setup(r => r.TryGetRequestMetadata(typeof(TestCommand), out metadata)).Returns(false);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _executor.ExecuteCommandAsync(command, CancellationToken.None));
        Assert.Contains("No metadata for request", exception.Message);
    }
}