// CQRSharp.Tests/Core/NotificationDispatcherTests.cs

using CQRSharp.Abstractions.Data.Interfaces.Notifications;
using CQRSharp.Abstractions.Data.Interfaces.Outbox;
using CQRSharp.Abstractions.Data.Interfaces.Transactions;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Contains unit tests for the <see cref="NotificationDispatcher" /> class.
/// </summary>
public class NotificationDispatcherTests
{
    private readonly Mock<IDirectNotificationDispatcher> _mockDirectDispatcher = new();
    private readonly Mock<IOutbox> _mockOutbox = new();
    private readonly Mock<IExplicitUnitOfWork> _mockUow = new();
    private readonly TestNotification _testNotification = new();
    private readonly UnstableTestNotification _unstableNotification = new();

    private sealed record UnstableTestNotification : INotification;

    /// <summary>
    ///     Builds a service provider with mocked dependencies for testing the dispatcher.
    /// </summary>
    private IServiceProvider BuildServiceProvider(Action<OutboxOptions> configureOptions, bool hasActiveTransaction)
    {
        var services = new ServiceCollection();
        services.Configure(configureOptions);

        services.AddSingleton(_mockDirectDispatcher.Object);
        services.AddScoped(_ => _mockOutbox.Object);

        _mockUow.Setup(u => u.HasActiveTransaction).Returns(hasActiveTransaction);
        services.AddScoped<IUnitOfWork>(_ => _mockUow.Object);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Publish_WhenOutboxIsDisabled_DispatchesDirectly()
    {
        // Arrange
        var provider = BuildServiceProvider(opts => opts.Mode = OutboxMode.Disabled, true);
        var dispatcher = new NotificationDispatcher(provider, provider.GetRequiredService<IOptions<OutboxOptions>>(), _mockDirectDispatcher.Object);

        // Act
        await dispatcher.Publish(_testNotification, CancellationToken.None);

        // Assert
        _mockDirectDispatcher.Verify(d => d.Publish(_testNotification, It.IsAny<CancellationToken>()), Times.Once);
        _mockOutbox.Verify(o => o.Add(It.IsAny<INotification>()), Times.Never);
    }

    [Fact]
    public async Task Publish_WhenOutboxIsEnabled_AddsToOutbox()
    {
        // Arrange
        var provider = BuildServiceProvider(opts => opts.Mode = OutboxMode.Enabled, false);
        var dispatcher = new NotificationDispatcher(provider, provider.GetRequiredService<IOptions<OutboxOptions>>(), _mockDirectDispatcher.Object);

        // Act
        await dispatcher.Publish(_testNotification, CancellationToken.None);

        // Assert
        _mockOutbox.Verify(o => o.Add(_testNotification), Times.Once);
        _mockDirectDispatcher.Verify(d => d.Publish(It.IsAny<INotification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Publish_WhenModeIsTransactional_And_NoTransaction_DispatchesDirectly()
    {
        // Arrange
        var provider = BuildServiceProvider(opts => opts.Mode = OutboxMode.Transactional, false);
        var dispatcher = new NotificationDispatcher(provider, provider.GetRequiredService<IOptions<OutboxOptions>>(), _mockDirectDispatcher.Object);

        // Act
        await dispatcher.Publish(_testNotification, CancellationToken.None);

        // Assert
        _mockDirectDispatcher.Verify(d => d.Publish(_testNotification, It.IsAny<CancellationToken>()), Times.Once);
        _mockOutbox.Verify(o => o.Add(It.IsAny<INotification>()), Times.Never);
    }

    [Fact]
    public async Task Publish_WhenModeIsTransactional_And_HasTransaction_AddsToOutbox()
    {
        // Arrange
        var provider = BuildServiceProvider(opts => opts.Mode = OutboxMode.Transactional, true);
        var dispatcher = new NotificationDispatcher(provider, provider.GetRequiredService<IOptions<OutboxOptions>>(), _mockDirectDispatcher.Object);

        // Act
        await dispatcher.Publish(_testNotification, CancellationToken.None);

        // Assert
        _mockOutbox.Verify(o => o.Add(_testNotification), Times.Once);
        _mockDirectDispatcher.Verify(d => d.Publish(It.IsAny<INotification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Publish_WhenOutboxEnabled_But_IOutboxNotRegistered_ThrowsException()
    {
        // Arrange
        var services = new ServiceCollection();
        services.Configure<OutboxOptions>(opts => opts.Mode = OutboxMode.Enabled);
        services.AddSingleton(_mockDirectDispatcher.Object);
        _mockUow.Setup(u => u.HasActiveTransaction).Returns(false);
        services.AddScoped<IUnitOfWork>(_ => _mockUow.Object);

        var provider = services.BuildServiceProvider();
        var dispatcher = new NotificationDispatcher(provider, provider.GetRequiredService<IOptions<OutboxOptions>>(), _mockDirectDispatcher.Object);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.Publish(_testNotification));
        Assert.Contains("IOutbox service is not registered", ex.Message);
    }

    [Fact]
    public async Task Publish_WhenOutboxIsEnabled_ButNotificationHasNoStableName_DispatchesDirectly()
    {
        // Arrange
        var provider = BuildServiceProvider(opts => opts.Mode = OutboxMode.Enabled, false);
        var dispatcher = new NotificationDispatcher(provider, provider.GetRequiredService<IOptions<OutboxOptions>>(), _mockDirectDispatcher.Object);

        // Act
        await dispatcher.Publish(_unstableNotification, CancellationToken.None);

        // Assert
        _mockDirectDispatcher.Verify(d => d.Publish(_unstableNotification, It.IsAny<CancellationToken>()), Times.Once);
        _mockOutbox.Verify(o => o.Add(It.IsAny<INotification>()), Times.Never);
    }
}
