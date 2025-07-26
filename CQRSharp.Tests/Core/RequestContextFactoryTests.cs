using CQRSharp.Abstractions.Data.Interfaces.Context;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Core.Factories;
using Moq;

namespace CQRSharp.Tests.Core;

/// <summary>
/// Contains unit tests for request context factory implementations.
/// </summary>
public class RequestContextFactoryTests
{
    [Fact]
    public void DefaultRequestContextFactory_CreateContext_ReturnsValidContext()
    {
        // Arrange
        var factory = new DefaultRequestContextFactory();
        var mockRequest = new Mock<IRequest>();
        var timeBefore = DateTime.UtcNow;

        // Act
        var context = factory.CreateContext(mockRequest.Object);
        var timeAfter = DateTime.UtcNow;

        // Assert
        Assert.NotNull(context);
        Assert.IsType<RequestContextBase>(context);
        Assert.True(context.CreatedAt >= timeBefore && context.CreatedAt <= timeAfter);
    }
}