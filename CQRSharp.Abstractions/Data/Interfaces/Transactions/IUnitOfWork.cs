namespace CQRSharp.Abstractions.Data.Interfaces.Transactions;

/// <summary>
/// Defines the contract for a Unit of Work, which manages transactions
/// to ensure that a series of operations are completed successfully or not at all.
/// </summary>
/// <remarks>
/// Implement this interface to work with a specific ORM or data access technology (e.g., Entity Framework Core).
/// The UoW is responsible for committing the transaction in <see cref="SaveChangesAsync"/> and
/// rolling back if DisposeAsync is called before a commit.
/// </remarks>
public interface IUnitOfWork : IAsyncDisposable
{
    /// <summary>
    /// Asynchronously saves all changes made in this unit of work to the underlying data store.
    /// This should commit the transaction.
    /// </summary>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous save operation. The task result contains the number of state entries written to the database.</returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
    
    /// <summary>
    /// Gets a transactional service, such as a DbContext or a repository,
    /// ensuring it participates in the current transaction.
    /// </summary>
    /// <typeparam name="TService">The type of the service to retrieve (e.g., MyDbContext).</typeparam>
    /// <returns>An instance of the requested service.</returns>
    TService GetService<TService>() where TService : class;
}