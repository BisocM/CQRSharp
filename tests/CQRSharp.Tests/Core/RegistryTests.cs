using System.Collections.Concurrent;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Tests.Shared;

namespace CQRSharp.Tests.Core;

public class HandlerRegistryTests
{
    [Fact]
    public void TryGetInvoker_ReturnsTheTypedInvoker_WhenHandlerExists()
    {
        var map = new ConcurrentDictionary<Type, Delegate>();
        var requestType = typeof(TestCommand);
        Func<object, TestCommand, CancellationToken, Task<CommandResult>> typed = (_, _, _) => Task.FromResult(CommandResult.FromSuccess());
        map[requestType] = typed;
        var registry = new HandlerRegistry(map);

        var invoker = registry.TryGetInvoker(requestType);

        Assert.Same(typed, invoker);
    }

    [Fact]
    public void TryGetInvoker_ReturnsNull_WhenHandlerNotFound()
    {
        var registry = new HandlerRegistry(new ConcurrentDictionary<Type, Delegate>());

        Assert.Null(registry.TryGetInvoker(typeof(TestCommand)));
    }
}

public class RequestRegistryTests
{
    [Fact]
    public void TryGetHandlerType_ReturnsHandlerType_WhenExists()
    {
        var map = new ConcurrentDictionary<Type, RequestMetadata>();
        var requestType = typeof(TestCommand);
        var handlerType = typeof(TestCommandHandler);
        var metadata = new RequestMetadata(requestType, handlerType, [], [], [], null, null);
        map[requestType] = metadata;
        var registry = new RequestRegistry(map);

        var actual = registry.TryGetHandlerType(requestType);

        Assert.Equal(handlerType, actual);
    }

    [Fact]
    public void TryGetHandlerType_ReturnsNull_WhenNotFound()
    {
        var registry = new RequestRegistry(new ConcurrentDictionary<Type, RequestMetadata>());

        var actual = registry.TryGetHandlerType(typeof(TestCommand));

        Assert.Null(actual);
    }

    [Fact]
    public void TryGetRequestMetadata_ReturnsMetadata_WhenExists()
    {
        var map = new ConcurrentDictionary<Type, RequestMetadata>();
        var requestType = typeof(TestCommand);
        var handlerType = typeof(TestCommandHandler);
        var metadata = new RequestMetadata(requestType, handlerType, [], [], [], null, null);
        map[requestType] = metadata;
        var registry = new RequestRegistry(map);

        var found = registry.TryGetRequestMetadata(requestType, out var result);

        Assert.True(found);
        Assert.NotNull(result);
        Assert.Equal(requestType, result.RequestType);
    }

    [Fact]
    public void TryGetRequestMetadata_ReturnsFalse_WhenNotFound()
    {
        var registry = new RequestRegistry(new ConcurrentDictionary<Type, RequestMetadata>());

        var found = registry.TryGetRequestMetadata(typeof(TestCommand), out var result);

        Assert.False(found);
        Assert.Null(result);
    }
}