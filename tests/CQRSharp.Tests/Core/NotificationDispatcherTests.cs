using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Abstractions.Interfaces.Transactions;
using CQRSharp.Core.Notifications;
using CQRSharp.Core.Options;
using CQRSharp.Core.Options.Enums;
using CQRSharp.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace CQRSharp.Tests.Core;

public sealed class NotificationDispatcherTests
{
    private readonly Mock<IDirectNotificationDispatcher> _mockDirectDispatcher = new();
    private readonly Mock<IOutbox> _mockOutbox = new();
    private readonly Mock<IExplicitUnitOfWork> _mockUow = new();
    private readonly TestNotification _testNotification = new();
    private readonly UnstableTestNotification _unstableNotification = new();

    private IServiceProvider BuildServiceProvider(Action<OutboxOptions> configureOptions, bool hasActiveTransaction, bool registerOutbox = true)
    {
        var services = new ServiceCollection();
        services.Configure(configureOptions);

        services.AddSingleton(_mockDirectDispatcher.Object);
        services.AddSingleton<IStableNotificationNameProvider, TestStableNotificationNameProvider>();

        _mockUow.Setup(u => u.HasActiveTransaction).Returns(hasActiveTransaction);
        services.AddScoped<IUnitOfWork>(_ => _mockUow.Object);

        if (registerOutbox)
            services.AddScoped(_ => _mockOutbox.Object);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Publish_WhenOutboxIsDisabled_DispatchesDirectly()
    {
        var provider = BuildServiceProvider(opts => opts.Mode = OutboxMode.Disabled, true);
        var dispatcher = new NotificationDispatcher(
            provider,
            provider.GetRequiredService<IOptions<OutboxOptions>>(),
            _mockDirectDispatcher.Object);

        await dispatcher.Publish(_testNotification, CancellationToken.None);

        _mockDirectDispatcher.Verify(d => d.Publish(_testNotification, It.IsAny<CancellationToken>()), Times.Once);
        _mockOutbox.Verify(o => o.Add(It.IsAny<INotification>()), Times.Never);
    }

    [Fact]
    public async Task Publish_WhenOutboxIsEnabled_AddsToOutbox()
    {
        var provider = BuildServiceProvider(opts => opts.Mode = OutboxMode.Enabled, false);
        var dispatcher = new NotificationDispatcher(
            provider,
            provider.GetRequiredService<IOptions<OutboxOptions>>(),
            _mockDirectDispatcher.Object);

        await dispatcher.Publish(_testNotification, CancellationToken.None);

        _mockOutbox.Verify(o => o.Add(_testNotification), Times.Once);
        _mockDirectDispatcher.Verify(d => d.Publish(It.IsAny<INotification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Publish_WhenModeIsTransactional_And_NoTransaction_DispatchesDirectly()
    {
        var provider = BuildServiceProvider(opts => opts.Mode = OutboxMode.Transactional, false);
        var dispatcher = new NotificationDispatcher(
            provider,
            provider.GetRequiredService<IOptions<OutboxOptions>>(),
            _mockDirectDispatcher.Object);

        await dispatcher.Publish(_testNotification, CancellationToken.None);

        _mockDirectDispatcher.Verify(d => d.Publish(_testNotification, It.IsAny<CancellationToken>()), Times.Once);
        _mockOutbox.Verify(o => o.Add(It.IsAny<INotification>()), Times.Never);
    }

    [Fact]
    public async Task Publish_WhenModeIsTransactional_And_HasTransaction_AddsToOutbox()
    {
        var provider = BuildServiceProvider(opts => opts.Mode = OutboxMode.Transactional, true);
        var dispatcher = new NotificationDispatcher(
            provider,
            provider.GetRequiredService<IOptions<OutboxOptions>>(),
            _mockDirectDispatcher.Object);

        await dispatcher.Publish(_testNotification, CancellationToken.None);

        _mockOutbox.Verify(o => o.Add(_testNotification), Times.Once);
        _mockDirectDispatcher.Verify(d => d.Publish(It.IsAny<INotification>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Publish_WhenOutboxEnabled_But_IOutboxNotRegistered_ThrowsException()
    {
        var provider = BuildServiceProvider(opts => opts.Mode = OutboxMode.Enabled, false, false);
        var dispatcher = new NotificationDispatcher(
            provider,
            provider.GetRequiredService<IOptions<OutboxOptions>>(),
            _mockDirectDispatcher.Object);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.Publish(_testNotification));
        Assert.Contains("IOutbox service is not registered", ex.Message);
    }

    [Fact]
    public async Task Publish_WhenOutboxIsEnabled_ButNotificationHasNoStableName_DispatchesDirectly()
    {
        var provider = BuildServiceProvider(opts => opts.Mode = OutboxMode.Enabled, false);
        var dispatcher = new NotificationDispatcher(
            provider,
            provider.GetRequiredService<IOptions<OutboxOptions>>(),
            _mockDirectDispatcher.Object);

        await dispatcher.Publish(_unstableNotification, CancellationToken.None);

        _mockDirectDispatcher.Verify(d => d.Publish(_unstableNotification, It.IsAny<CancellationToken>()), Times.Once);
        _mockOutbox.Verify(o => o.Add(It.IsAny<INotification>()), Times.Never);
    }

    private sealed record UnstableTestNotification : INotification;

    private sealed class TestStableNotificationNameProvider : IStableNotificationNameProvider
    {
        public bool TryGetStableName(Type notificationType, out string stableName)
        {
            if (notificationType == typeof(TestNotification))
            {
                stableName = "test.notification";
                return true;
            }

            stableName = string.Empty;
            return false;
        }
    }
}