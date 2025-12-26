// CQRSharp.Tests/Core/PipelineExecutorTests.cs

using CQRSharp.Abstractions.Data.Attributes.Pipelines;
using CQRSharp.Abstractions.Data.Interfaces.Context;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Abstractions.Data.Models.Requests;
using CQRSharp.Core.Background.TaskQueue;
using CQRSharp.Core.Caching.Contexts;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Core.Factories;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Notifications.Types;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Core.Pipelines;
using CQRSharp.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
	    private readonly Mock<IBackgroundTaskManager> _mockBackgroundTaskManager;
	    private readonly Mock<IServiceProvider> _mockRootProvider;
	    private readonly Mock<IContextFactoryRegistry> _mockContextFactoryRegistry;
	    private readonly Mock<IRequestRegistry> _mockRequestRegistry;
	    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
	    private readonly Mock<IServiceProvider> _mockScopedProvider;
	
	    public PipelineExecutorTests()
	    {
	        _mockRootProvider = new Mock<IServiceProvider>();
	        _mockScopeFactory = new Mock<IServiceScopeFactory>();
	        var mockScope = new Mock<IServiceScope>();
	        _mockScopedProvider = new Mock<IServiceProvider>();
	        _mockRequestRegistry = new Mock<IRequestRegistry>();
	        _mockHandlerRegistry = new Mock<IHandlerRegistry>();
	        _mockContextFactoryRegistry = new Mock<IContextFactoryRegistry>();
	        _mockNotificationDispatcher = new Mock<INotificationDispatcher>();

	        mockScope.Setup(s => s.ServiceProvider).Returns(_mockScopedProvider.Object);
	        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
	        _mockRootProvider.Setup(p => p.GetService(typeof(IServiceScopeFactory))).Returns(_mockScopeFactory.Object);

	        _mockRootProvider.Setup(p => p.GetService(typeof(INotificationDispatcher))).Returns(_mockNotificationDispatcher.Object);
	        _mockRootProvider
	            .Setup(p => p.GetService(typeof(IEnumerable<IPipelineBehavior<TestCommand, CommandResult>>)))
	            .Returns(Array.Empty<IPipelineBehavior<TestCommand, CommandResult>>());

	        _mockScopedProvider.Setup(p => p.GetService(typeof(INotificationDispatcher))).Returns(_mockNotificationDispatcher.Object);
	        _mockScopedProvider
	            .Setup(p => p.GetService(typeof(IEnumerable<IPipelineBehavior<TestCommand, CommandResult>>)))
	            .Returns(Array.Empty<IPipelineBehavior<TestCommand, CommandResult>>());

	        var mockContextFactory = new Mock<IInternalRequestContextFactory>();
	        mockContextFactory.Setup(f => f.CreateContext(It.IsAny<IRequest>())).Returns(new RequestContextBase());
	        _mockContextFactoryRegistry.Setup(r => r.TryGetFactory(It.IsAny<Type>(), It.IsAny<IServiceProvider>())).Returns(mockContextFactory.Object);

        _mockBackgroundTaskManager = new Mock<IBackgroundTaskManager>();
        var dispatcherOptions = Options.Create(new DispatcherOptions { RunMode = RunMode.Sync });

	        _executor = new PipelineExecutor(
	            _mockRootProvider.Object,
	            _mockRequestRegistry.Object,
	            _mockHandlerRegistry.Object,
	            _mockContextFactoryRegistry.Object,
	            dispatcherOptions,
	            _mockBackgroundTaskManager.Object
	        );
	    }

	    private void SetupHandlerResolution<THandler>(THandler handler) where THandler : class
	    {
	        _mockRootProvider.Setup(p => p.GetService(typeof(THandler))).Returns(handler);
	        _mockScopedProvider.Setup(p => p.GetService(typeof(THandler))).Returns(handler);
	    }

    [Fact]
    public async Task ExecuteCommandAsync_WhenBehaviorIsExempted_SkipsIt()
    {
        // Arrange
        var command = new TestCommand();
        var handler = new TestCommandHandler();
        var requestType = typeof(TestCommand);
        var handlerType = typeof(TestCommandHandler);
        var callOrder = new List<string>();

        var exempted = new ExemptedBehavior<TestCommand, CommandResult>(callOrder);
        var other = new OtherBehavior<TestCommand, CommandResult>(callOrder);

	        _mockRootProvider
	            .Setup(p => p.GetService(typeof(IEnumerable<IPipelineBehavior<TestCommand, CommandResult>>)))
	            .Returns(new IPipelineBehavior<TestCommand, CommandResult>[] { exempted, other });

        var exemptions = new[] { new PipelineExemptionAttribute(typeof(ExemptedBehavior<,>)) };
        RequestMetadata metadata = new(requestType, handlerType, [], [], exemptions, null, typeof(RequestContextBase));

        _mockRequestRegistry.Setup(r => r.TryGetRequestMetadata(requestType, out metadata!)).Returns(true);
        _mockRequestRegistry.Setup(r => r.TryGetHandlerType(requestType)).Returns(handlerType);
        _mockHandlerRegistry.Setup(r => r.TryGetHandlerDelegate(requestType, out It.Ref<HandlerInvokerDelegate>.IsAny))
            .Returns((Type _, out HandlerInvokerDelegate del) =>
            {
	                del = (h, r, c) =>
	                {
	                    callOrder.Add("handle");
	                    return ((TestCommandHandler)h).Handle((TestCommand)r, c)
	                        .ContinueWith(t => (object?)t.Result, TaskScheduler.Default);
	                };
	                return true;
	            });

        SetupHandlerResolution(handler);

        // Act
        await _executor.ExecuteCommandAsync(command, CancellationToken.None);

        // Assert
        Assert.Contains("handle", callOrder);
        Assert.Contains("other", callOrder);
        Assert.DoesNotContain("exempted", callOrder);
    }

    [Fact]
    public async Task ExecuteCommandAsync_InvokesHandlerAndPublishesNotifications()
    {
        // Arrange
        var command = new TestCommand();
        var handler = new TestCommandHandler();
        var requestType = typeof(TestCommand);
        var handlerType = typeof(TestCommandHandler);
        RequestMetadata metadata = new(requestType, handlerType, [], [], [], null, typeof(RequestContextBase));

        _mockRequestRegistry.Setup(r => r.TryGetRequestMetadata(requestType, out metadata!)).Returns(true);
        _mockRequestRegistry.Setup(r => r.TryGetHandlerType(requestType)).Returns(handlerType);
        _mockHandlerRegistry.Setup(r => r.TryGetHandlerDelegate(requestType, out It.Ref<HandlerInvokerDelegate>.IsAny))
            .Returns((Type _, out HandlerInvokerDelegate del) =>
            {
	                del = (h, r, c) => ((TestCommandHandler)h).Handle((TestCommand)r, c).ContinueWith(t => (object?)t.Result, TaskScheduler.Default);
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
    public async Task ExecuteCommandAsync_WhenRunModeIsAsync_EnqueuesAndDefersExecution()
    {
        // Arrange
        var executor = new PipelineExecutor(
            _mockRootProvider.Object,
            _mockRequestRegistry.Object,
            _mockHandlerRegistry.Object,
            _mockContextFactoryRegistry.Object,
            Options.Create(new DispatcherOptions { RunMode = RunMode.Async }),
            _mockBackgroundTaskManager.Object);

        var command = new TestCommand();
        var handler = new TestCommandHandler();
        var requestType = typeof(TestCommand);
        var handlerType = typeof(TestCommandHandler);
        RequestMetadata metadata = new(requestType, handlerType, [], [], [], null, typeof(RequestContextBase));

        _mockRequestRegistry.Setup(r => r.TryGetRequestMetadata(requestType, out metadata!)).Returns(true);
        _mockRequestRegistry.Setup(r => r.TryGetHandlerType(requestType)).Returns(handlerType);

        var handled = false;
        _mockHandlerRegistry
            .Setup(r => r.TryGetHandlerDelegate(requestType, out It.Ref<HandlerInvokerDelegate>.IsAny))
            .Returns((Type _, out HandlerInvokerDelegate del) =>
            {
	                del = (h, r, c) =>
	                {
	                    handled = true;
	                    return ((TestCommandHandler)h).Handle((TestCommand)r, c).ContinueWith(t => (object?)t.Result, TaskScheduler.Default);
	                };
	                return true;
	            });

        SetupHandlerResolution(handler);

        Func<CancellationToken, Task<CommandResult>>? capturedWorkItem = null;
        var queuedResult = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockBackgroundTaskManager
            .Setup(m => m.EnqueueAsync<CommandResult>(It.IsAny<Func<CancellationToken, Task<CommandResult>>>(), It.IsAny<CancellationToken>()))
            .Callback<Func<CancellationToken, Task<CommandResult>>, CancellationToken>((work, _) => capturedWorkItem = work)
            .Returns(queuedResult.Task);

        // Act - should only enqueue, not execute inline.
        var resultTask = executor.ExecuteCommandAsync(command, CancellationToken.None);

        // Assert - deferred until the background work item is run.
        Assert.False(handled);
        _mockNotificationDispatcher.Verify(d => d.Publish(It.IsAny<CommandInitiatedNotification>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.NotNull(capturedWorkItem);

        var backgroundResult = await capturedWorkItem!(CancellationToken.None);
        queuedResult.SetResult(backgroundResult);

        var final = await resultTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(final.IsSuccess);
        Assert.True(handled);
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
            [], null, typeof(RequestContextBase));

        _mockRequestRegistry.Setup(r => r.TryGetRequestMetadata(requestType, out metadata!)).Returns(true);
        _mockRequestRegistry.Setup(r => r.TryGetHandlerType(requestType)).Returns(handlerType);
        _mockHandlerRegistry.Setup(r => r.TryGetHandlerDelegate(requestType, out It.Ref<HandlerInvokerDelegate>.IsAny))
            .Returns((Type _, out HandlerInvokerDelegate del) =>
            {
	                del = (h, r, c) =>
	                {
	                    callOrder.Add("handle");
	                    return ((TestCommandHandler)h).Handle((TestCommand)r, c).ContinueWith(t => (object?)t.Result, TaskScheduler.Default);
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

    private sealed class ExemptedBehavior<TRequest, TResult>(List<string> callOrder)
        : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
    {
        public Task<TResult> Handle(TRequest request, Func<CancellationToken, Task<TResult>> next,
            CancellationToken cancellationToken)
        {
            callOrder.Add("exempted");
            return next(cancellationToken);
        }
    }

    private sealed class OtherBehavior<TRequest, TResult>(List<string> callOrder)
        : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
    {
        public Task<TResult> Handle(TRequest request, Func<CancellationToken, Task<TResult>> next,
            CancellationToken cancellationToken)
        {
            callOrder.Add("other");
            return next(cancellationToken);
        }
    }
}
