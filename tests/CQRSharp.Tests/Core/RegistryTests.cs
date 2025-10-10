using System.Collections.Concurrent;
using CQRSharp.Abstractions.Data.Models.Requests;
using CQRSharp.Core.Caching.Handlers;
using CQRSharp.Core.Caching.Pipelines;
using CQRSharp.Core.Caching.Requests;
using CQRSharp.Tests.Shared;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Contains unit tests for the <see cref="HandlerRegistry" /> class.
/// </summary>
public class HandlerRegistryTests
{
    [Fact]
    public void TryGetHandlerDelegate_ReturnsTrue_WhenHandlerExists()
    {
        // Arrange
        var map = new ConcurrentDictionary<Type, HandlerInvokerDelegate>();
        var requestType = typeof(TestCommand);
        map[requestType] = (handler, request, ct) => Task.FromResult<object>("test");
        var registry = new HandlerRegistry(map);

        // Act
        var found = registry.TryGetHandlerDelegate(requestType, out var invoker);

        // Assert
        Assert.True(found);
        Assert.NotNull(invoker);
    }

    [Fact]
    public void TryGetHandlerDelegate_ReturnsFalse_WhenHandlerNotFound()
    {
        // Arrange
        var map = new ConcurrentDictionary<Type, HandlerInvokerDelegate>();
        var registry = new HandlerRegistry(map);

        // Act
        var found = registry.TryGetHandlerDelegate(typeof(TestCommand), out var invoker);

        // Assert
        Assert.False(found);
        Assert.Null(invoker);
    }
}

/// <summary>
///     Contains unit tests for the <see cref="PipelineRegistry" /> class.
/// </summary>
public class PipelineRegistryTests
{
    [Fact]
    public void GetPipelineBuilder_ReturnsExpectedDelegate()
    {
        // Arrange
        var dic = new ConcurrentDictionary<Type, PipelineBuilderDelegate>();
        var key = typeof(string);
        PipelineBuilderDelegate builder = (services, request, finalHandler, token) => finalHandler(token);
        dic[key] = builder;
        var registry = new PipelineRegistry(dic);

        // Act
        var retrieved = registry.GetPipelineBuilder(key);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(builder, retrieved);
    }

    [Fact]
    public void GetPipelineBuilder_ReturnsNullForMissingKey()
    {
        // Arrange
        var dic = new ConcurrentDictionary<Type, PipelineBuilderDelegate>();
        var registry = new PipelineRegistry(dic);

        // Act
        var builder = registry.GetPipelineBuilder(typeof(int));

        // Assert
        Assert.Null(builder);
    }
}

/// <summary>
///     Contains unit tests for the <see cref="RequestRegistry" /> class.
/// </summary>
public class RequestRegistryTests
{
    [Fact]
    public void TryGetHandlerType_ReturnsHandlerType_WhenExists()
    {
        // Arrange
        var map = new ConcurrentDictionary<Type, RequestMetadata>();
        var requestType = typeof(TestCommand);
        var handlerType = typeof(TestCommandHandler);
        var metadata = new RequestMetadata(requestType, handlerType, [], [], [], [], null, null);
        map[requestType] = metadata;
        var registry = new RequestRegistry(map);

        // Act
        var actual = registry.TryGetHandlerType(requestType);

        // Assert
        Assert.Equal(handlerType, actual);
    }

    [Fact]
    public void TryGetHandlerType_ReturnsNull_WhenNotFound()
    {
        // Arrange
        var registry = new RequestRegistry(new ConcurrentDictionary<Type, RequestMetadata>());

        // Act
        var actual = registry.TryGetHandlerType(typeof(TestCommand));

        // Assert
        Assert.Null(actual);
    }

    [Fact]
    public void TryGetRequestMetadata_ReturnsMetadata_WhenExists()
    {
        // Arrange
        var map = new ConcurrentDictionary<Type, RequestMetadata>();
        var requestType = typeof(TestCommand);
        var handlerType = typeof(TestCommandHandler);
        var metadata = new RequestMetadata(requestType, handlerType, [], [], [], [], null, null);
        map[requestType] = metadata;
        var registry = new RequestRegistry(map);

        // Act
        var found = registry.TryGetRequestMetadata(requestType, out var result);

        // Assert
        Assert.True(found);
        Assert.NotNull(result);
        Assert.Equal(requestType, result.RequestType);
    }

    [Fact]
    public void TryGetRequestMetadata_ReturnsFalse_WhenNotFound()
    {
        // Arrange
        var registry = new RequestRegistry(new ConcurrentDictionary<Type, RequestMetadata>());

        // Act
        var found = registry.TryGetRequestMetadata(typeof(TestCommand), out var result);

        // Assert
        Assert.False(found);
        Assert.Null(result);
    }
}