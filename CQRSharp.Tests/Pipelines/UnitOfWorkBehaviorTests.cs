using System.Data;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Data.Interfaces.Transactions;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Pipelines.Options;
using CQRSharp.Pipelines.Types.Transactions;
using CQRSharp.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
/// Contains unit tests for the <see cref="UnitOfWorkBehavior{TRequest,TResponse}"/> class.
/// </summary>
public class UnitOfWorkBehaviorTests
{
    private readonly Mock<ILogger<UnitOfWorkBehavior<ICommand, CommandResult>>> _mockLogger;
    private readonly Mock<IUnitOfWork> _mockUoW;
    private readonly IOptions<UnitOfWorkOptions> _options;
    private readonly IServiceProvider _serviceProvider;

    public UnitOfWorkBehaviorTests()
    {
        _mockLogger = new Mock<ILogger<UnitOfWorkBehavior<ICommand, CommandResult>>>();
        _mockUoW = new Mock<IUnitOfWork>();
        _options = Options.Create(new UnitOfWorkOptions { DefaultIsolationLevel = IsolationLevel.ReadCommitted });

        var services = new ServiceCollection();
        services.AddScoped<IUnitOfWork>(_ => _mockUoW.Object);
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact(DisplayName = "UoW Behavior should commit when handler succeeds for a transactional request")]
    public async Task Handle_ShouldCommitUoW_WhenHandlerSucceeds()
    {
        // Arrange
        var behavior = new UnitOfWorkBehavior<ICommand, CommandResult>(_mockLogger.Object, _serviceProvider, _options);
        var command = new TransactionalCommand();
        var nextDelegate = new Mock<Func<CancellationToken, Task<CommandResult>>>();
        nextDelegate.Setup(next => next(It.IsAny<CancellationToken>())).ReturnsAsync(CommandResult.FromSuccess());

        // Act
        var result = await behavior.Handle(command, nextDelegate.Object, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        nextDelegate.Verify(next => next(It.IsAny<CancellationToken>()), Times.Once);
        _mockUoW.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(DisplayName = "UoW Behavior should roll back when handler fails for a transactional request")]
    public async Task Handle_ShouldRollbackUoW_WhenHandlerFails()
    {
        // Arrange
        var behavior = new UnitOfWorkBehavior<ICommand, CommandResult>(_mockLogger.Object, _serviceProvider, _options);
        var command = new TransactionalCommand();
        var nextDelegate = new Mock<Func<CancellationToken, Task<CommandResult>>>();
        var exception = new InvalidOperationException("Handler failed");
        nextDelegate.Setup(next => next(It.IsAny<CancellationToken>())).ThrowsAsync(exception);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            behavior.Handle(command, nextDelegate.Object, CancellationToken.None));

        nextDelegate.Verify(next => next(It.IsAny<CancellationToken>()), Times.Once());
        _mockUoW.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _mockUoW.Verify(uow => uow.DisposeAsync(), Times.Once);
    }

    [Fact(DisplayName = "UoW Behavior should bypass logic for a non-transactional request")]
    public async Task Handle_ShouldBypassUoW_ForNonTransactionalRequest()
    {
        // Arrange
        var behavior = new UnitOfWorkBehavior<ICommand, CommandResult>(_mockLogger.Object, _serviceProvider, _options);
        var command = new NonTransactionalCommand();
        var nextDelegate = new Mock<Func<CancellationToken, Task<CommandResult>>>();
        nextDelegate.Setup(next => next(It.IsAny<CancellationToken>())).ReturnsAsync(CommandResult.FromSuccess());

        // Act
        var result = await behavior.Handle(command, nextDelegate.Object, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        nextDelegate.Verify(next => next(It.IsAny<CancellationToken>()), Times.Once);
        _mockUoW.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _mockUoW.Verify(uow => uow.DisposeAsync(), Times.Never);
    }
}