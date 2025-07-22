using System.Collections.Concurrent;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Pipelines;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Data.Models.Requests;

namespace CQRSharp.Tests;

#region HandlerRegistryTests

public class HandlerRegistryTests
{
    [Fact]
    public void TryGetHandlerDelegate_ReturnsTrue_WhenHandlerExists()
    {
        var map = new ConcurrentDictionary<Type, HandlerInvokerDelegate>();
        var dummyType = typeof(string); // Just a random type
        map[dummyType] = (handler, request, ct) => Task.FromResult<object>("test");

        var registry = new HandlerRegistry(map);

        var found = registry.TryGetHandlerDelegate(dummyType, out var invoker);
        Assert.True(found);
        Assert.NotNull(invoker);
    }

    [Fact]
    public void TryGetHandlerDelegate_ReturnsFalse_WhenHandlerNotFound()
    {
        var map = new ConcurrentDictionary<Type, HandlerInvokerDelegate>();
        var registry = new HandlerRegistry(map);

        var found = registry.TryGetHandlerDelegate(typeof(int), out var invoker);
        Assert.False(found);
        Assert.Null(invoker);
    }
}

#endregion

#region PipelineRegistryTests

public class PipelineRegistryTests
{
    [Fact]
    public void GetPipelineBuilder_ReturnsExpectedDelegate()
    {
        var dic = new ConcurrentDictionary<Type, PipelineBuilderDelegate>();
        var key = typeof(string);
        PipelineBuilderDelegate builder = (services, request, finalHandler, token) =>
        {
            // pipeline code here
            return finalHandler(token);
        };
        dic[key] = builder;

        var registry = new PipelineRegistry(dic);

        var retrieved = registry.GetPipelineBuilder(key);
        Assert.NotNull(retrieved);
        Assert.Equal(builder, retrieved);
    }

    [Fact]
    public void GetPipelineBuilder_ReturnsNullForMissingKey()
    {
        var dic = new ConcurrentDictionary<Type, PipelineBuilderDelegate>();
        var registry = new PipelineRegistry(dic);

        var builder = registry.GetPipelineBuilder(typeof(int));
        Assert.Null(builder);
    }
}

#endregion

#region RequestRegistryTests

public class RequestRegistryTests
{
    [Fact]
    public void TryGetHandlerType_ReturnsHandlerType_WhenExists()
    {
        var map = new ConcurrentDictionary<Type, RequestMetadata>();
        var requestType = typeof(TestCommand);
        var handlerType = typeof(TestCommandHandler);

        var metadata = new RequestMetadata(
            requestType,
            handlerType,
            [],
            [],
            [],
            [],
            null,
            null
        );
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
        var metadata = new RequestMetadata(
            requestType,
            typeof(TestCommandHandler),
            [],
            [],
            [],
            [],
            null,
            null
        );
        map[requestType] = metadata;

        var registry = new RequestRegistry(map);

        var found = registry.TryGetRequestMetadata(requestType, out var result);
        Assert.True(found);
        Assert.NotNull(result);
        Assert.Equal(typeof(TestCommand), result!.RequestType);
    }

    [Fact]
    public void TryGetRequestMetadata_ReturnsFalse_WhenNotFound()
    {
        var registry = new RequestRegistry(new ConcurrentDictionary<Type, RequestMetadata>());
        var found = registry.TryGetRequestMetadata(typeof(TestCommand), out var result);
        Assert.False(found);
        Assert.Null(result);
    }

    // Dummy classes for testing
    private class TestCommand : CommandBase
    {
    }

    private class TestCommandHandler
    {
    }
}

#endregion