using System.Data;
using CQRSharp.Abstractions.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Abstractions.Interfaces.Transactions;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines.Behaviors.Transactions;
using CQRSharp.Pipelines.Options;
using CQRSharp.Tests.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Contains unit tests for the <see cref="UnitOfWorkBehavior{TRequest,TResponse}" /> class.
/// </summary>
public class UnitOfWorkBehaviorTests
{
    private readonly Mock<ILogger<UnitOfWorkBehavior<ICommand, CommandResult>>> _mockLogger;
    private readonly Mock<IOutbox> _mockOutbox;
    private readonly Mock<IUnitOfWork> _mockUoW;
    private readonly IOptions<UnitOfWorkOptions> _options;

    public UnitOfWorkBehaviorTests()
    {
        _mockLogger = new Mock<ILogger<UnitOfWorkBehavior<ICommand, CommandResult>>>();
        _mockOutbox = new Mock<IOutbox>();
        _mockUoW = new Mock<IUnitOfWork>();
        _options = Options.Create(new UnitOfWorkOptions { DefaultIsolationLevel = IsolationLevel.ReadCommitted });
        _mockOutbox.Setup(o => o.Drain()).Returns(Array.Empty<INotification>());
    }

    [Fact(DisplayName = "UoW Behavior should commit when handler succeeds for a transactional request")]
    public async Task Handle_ShouldCommitUoW_WhenHandlerSucceeds()
    {
        // Arrange
        var behavior = new UnitOfWorkBehavior<ICommand, CommandResult>(_mockLogger.Object, _mockUoW.Object, _mockOutbox.Object, _options);
        var command = new TransactionalCommand();
        var nextDelegate = new Mock<Func<CancellationToken, Task<CommandResult>>>();
        nextDelegate.Setup(next => next(It.IsAny<CancellationToken>())).ReturnsAsync(CommandResult.FromSuccess());

        // Act
        var result = await behavior.Handle(command, new RequestHandlerDelegate<CommandResult>(nextDelegate.Object), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        nextDelegate.Verify(next => next(It.IsAny<CancellationToken>()), Times.Once);
        _mockUoW.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(DisplayName = "UoW Behavior does not save when the handler returns a failed CommandResult, and drops its notifications")]
    public async Task Handle_ShouldNotSave_WhenHandlerReturnsFailedResult()
    {
        var behavior = new UnitOfWorkBehavior<ICommand, CommandResult>(_mockLogger.Object, _mockUoW.Object, _mockOutbox.Object, _options);
        var nextDelegate = new Mock<Func<CancellationToken, Task<CommandResult>>>();
        nextDelegate.Setup(next => next(It.IsAny<CancellationToken>())).ReturnsAsync(CommandResult.FromError("insufficient funds"));

        var result = await behavior.Handle(new TransactionalCommand(), new RequestHandlerDelegate<CommandResult>(nextDelegate.Object), CancellationToken.None);

        Assert.False(result.IsSuccess);
        _mockUoW.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _mockOutbox.Verify(outbox => outbox.Drain(), Times.Once, "the failed command's notifications are discarded");
    }

    [Fact(DisplayName = "UoW Behavior still saves a failed CommandResult when RollbackOnFailedResult is off")]
    public async Task Handle_ShouldSave_WhenRollbackOnFailedResultIsDisabled()
    {
        var options = Options.Create(new UnitOfWorkOptions { RollbackOnFailedResult = false });
        var behavior = new UnitOfWorkBehavior<ICommand, CommandResult>(_mockLogger.Object, _mockUoW.Object, _mockOutbox.Object, options);
        _mockOutbox.Setup(outbox => outbox.Drain()).Returns(Array.Empty<CQRSharp.Abstractions.Interfaces.Notifications.INotification>());
        var nextDelegate = new Mock<Func<CancellationToken, Task<CommandResult>>>();
        nextDelegate.Setup(next => next(It.IsAny<CancellationToken>())).ReturnsAsync(CommandResult.FromError("recorded anyway"));

        await behavior.Handle(new TransactionalCommand(), new RequestHandlerDelegate<CommandResult>(nextDelegate.Object), CancellationToken.None);

        _mockUoW.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(DisplayName = "UoW Behavior should roll back when handler fails for a transactional request")]
    public async Task Handle_ShouldRollbackUoW_WhenHandlerFails()
    {
        // Arrange
        var behavior = new UnitOfWorkBehavior<ICommand, CommandResult>(_mockLogger.Object, _mockUoW.Object, _mockOutbox.Object, _options);
        var command = new TransactionalCommand();
        var nextDelegate = new Mock<Func<CancellationToken, Task<CommandResult>>>();
        var exception = new InvalidOperationException("Handler failed");
        nextDelegate.Setup(next => next(It.IsAny<CancellationToken>())).ThrowsAsync(exception);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            behavior.Handle(command, new RequestHandlerDelegate<CommandResult>(nextDelegate.Object), CancellationToken.None));

        nextDelegate.Verify(next => next(It.IsAny<CancellationToken>()), Times.Once());
        _mockUoW.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact(DisplayName = "UoW Behavior should bypass logic for a non-transactional request")]
    public async Task Handle_ShouldBypassUoW_ForNonTransactionalRequest()
    {
        // Arrange
        var behavior = new UnitOfWorkBehavior<ICommand, CommandResult>(_mockLogger.Object, _mockUoW.Object, _mockOutbox.Object, _options);
        var command = new NonTransactionalCommand();
        var nextDelegate = new Mock<Func<CancellationToken, Task<CommandResult>>>();
        nextDelegate.Setup(next => next(It.IsAny<CancellationToken>())).ReturnsAsync(CommandResult.FromSuccess());

        // Act
        var result = await behavior.Handle(command, new RequestHandlerDelegate<CommandResult>(nextDelegate.Object), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        nextDelegate.Verify(next => next(It.IsAny<CancellationToken>()), Times.Once);
        _mockUoW.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}