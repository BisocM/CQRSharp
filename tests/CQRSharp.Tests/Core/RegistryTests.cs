using System.Collections.Concurrent;
using CQRSharp.Abstractions.Data.Models.Requests;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Tests.Shared;

namespace CQRSharp.Tests.Core;

public class HandlerRegistryTests
{
    [Fact]
    public void TryGetHandlerDelegate_ReturnsTrue_WhenHandlerExists()
    {
        var map = new ConcurrentDictionary<Type, HandlerInvokerDelegate>();
        var requestType = typeof(TestCommand);
        map[requestType] = (_, _, _) => Task.FromResult<object>("test");
        var registry = new HandlerRegistry(map);

        var found = registry.TryGetHandlerDelegate(requestType, out var invoker);

        Assert.True(found);
        Assert.NotNull(invoker);
    }

    [Fact]
    public void TryGetHandlerDelegate_ReturnsFalse_WhenHandlerNotFound()
    {
        var map = new ConcurrentDictionary<Type, HandlerInvokerDelegate>();
        var registry = new HandlerRegistry(map);

        var found = registry.TryGetHandlerDelegate(typeof(TestCommand), out var invoker);

        Assert.False(found);
        Assert.Null(invoker);
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
