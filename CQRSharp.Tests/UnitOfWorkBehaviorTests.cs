using System.Data;
using CQRSharp.Abstractions.Data.Interfaces.Markers.Command;
using CQRSharp.Abstractions.Data.Models.Commands;
using CQRSharp.Pipelines.Options;
using CQRSharp.Pipelines.Types.Transactions;
using CQRSharp.Pipelines.Types.Transactions.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace CQRSharp.Tests;

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

        // Set up a service provider that can resolve our mock IUnitOfWork within a scope.
        // This is a robust way to test behavior that depends on DI scopes.
        var services = new ServiceCollection();
        services.AddScoped<IUnitOfWork>(_ => _mockUoW.Object);
        _serviceProvider = services.BuildServiceProvider();
    }

    /// <summary>
    /// A command that is marked to be wrapped in a transaction.
    /// </summary>
    private class TransactionalCommand : CommandBase, ITransactionalRequest
    {
        public IsolationLevel IsolationLevel { get; set; }
    }

    /// <summary>
    /// A command that is *not* marked as transactional.
    /// </summary>
    private class NonTransactionalCommand : CommandBase
    {
    }

    [Fact(DisplayName = "UoW Behavior should commit when handler succeeds for a transactional request")]
    public async Task Handle_ShouldCommitUoW_WhenHandlerSucceeds()
    {
        // Arrange
        var behavior = new UnitOfWorkBehavior<ICommand, CommandResult>(_mockLogger.Object, _serviceProvider, _options);
        var command = new TransactionalCommand();
        var nextDelegate = new Mock<Func<CancellationToken, Task<CommandResult>>>();
        nextDelegate.Setup(next => next(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.FromSuccess());

        // Act
        var result = await behavior.Handle(command, nextDelegate.Object, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        
        // Verify that the next handler in the pipeline was called.
        nextDelegate.Verify(next => next(It.IsAny<CancellationToken>()), Times.Once);
        
        // Verify that the transaction was committed.
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
        
        nextDelegate.Setup(next => next(It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => 
            behavior.Handle(command, nextDelegate.Object, CancellationToken.None));
        
        // Verify that the next handler in the pipeline was called.
        nextDelegate.Verify(next => next(It.IsAny<CancellationToken>()), Times.Once);

        // Verify that the transaction was *not* committed due to the exception.
        _mockUoW.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);

        // Verify that the UoW was disposed, which implies a rollback.
        _mockUoW.Verify(uow => uow.DisposeAsync(), Times.Once);
    }
    
    [Fact(DisplayName = "UoW Behavior should bypass logic for a non-transactional request")]
    public async Task Handle_ShouldBypassUoW_ForNonTransactionalRequest()
    {
        // Arrange
        var behavior = new UnitOfWorkBehavior<ICommand, CommandResult>(_mockLogger.Object, _serviceProvider, _options);
        var command = new NonTransactionalCommand();
        var nextDelegate = new Mock<Func<CancellationToken, Task<CommandResult>>>();
        
        nextDelegate.Setup(next => next(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CommandResult.FromSuccess());

        // Act
        var result = await behavior.Handle(command, nextDelegate.Object, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);

        // Verify that the next handler was called.
        nextDelegate.Verify(next => next(It.IsAny<CancellationToken>()), Times.Once);

        // Verify that no transaction logic was ever invoked.
        _mockUoW.Verify(uow => uow.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _mockUoW.Verify(uow => uow.DisposeAsync(), Times.Never);
    }
}