using System.Data;
using CQRSharp.EntityFrameworkCore;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

/// <summary>
///     <see cref="EfCoreUnitOfWork{TContext}" /> on SQLite: a commit saves what the handler left tracked, a rollback
///     leaves nothing behind for the next save in the scope, the unit of work follows the context's own transaction, and
///     the pipeline over it keeps a retried or failed command from committing twice or at all.
/// </summary>
public sealed class EfCoreUnitOfWorkTests : IAsyncDisposable
{
    private readonly SqliteFileDatabase _database = new();

    private string ConnectionString => _database.ConnectionString;

    private OrdersDbContext NewContext(Action<Microsoft.EntityFrameworkCore.Infrastructure.SqliteDbContextOptionsBuilder>? sqlite = null)
        => new(new DbContextOptionsBuilder<OrdersDbContext>().UseSqlite(ConnectionString, sqlite).Options);

    private async Task<OrdersDbContext> CreatedContextAsync()
    {
        var context = NewContext();
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    private async Task<int> OrderCountAsync()
    {
        await using var context = NewContext();
        return await context.Orders.CountAsync();
    }

    [Fact(DisplayName = "A commit saves the changes the handler left tracked, then commits")]
    public async Task Commit_saves_pending_changes()
    {
        await using var context = await CreatedContextAsync();
        var unitOfWork = new EfCoreUnitOfWork<OrdersDbContext>(context);

        await unitOfWork.BeginTransactionAsync(IsolationLevel.Unspecified, CancellationToken.None);
        context.Orders.Add(new Order { Text = "tracked only" });
        await unitOfWork.CommitAsync(CancellationToken.None);

        unitOfWork.HasActiveTransaction.Should().BeFalse();
        (await OrderCountAsync()).Should().Be(1, "the commit persisted what was never saved explicitly");
    }

    [Fact(DisplayName = "A rollback undoes saved changes and clears the tracker, so a later save in the scope writes nothing")]
    public async Task Rollback_discards_saved_and_tracked_changes()
    {
        await using var context = await CreatedContextAsync();
        var unitOfWork = new EfCoreUnitOfWork<OrdersDbContext>(context);

        await unitOfWork.BeginTransactionAsync(IsolationLevel.Unspecified, CancellationToken.None);
        context.Orders.Add(new Order { Text = "saved in the transaction" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.Orders.Add(new Order { Text = "tracked only" });
        await unitOfWork.RollbackAsync(CancellationToken.None);

        unitOfWork.HasActiveTransaction.Should().BeFalse();
        context.ChangeTracker.Entries().Should().BeEmpty();
        (await context.SaveChangesAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await OrderCountAsync()).Should().Be(0);
    }

    [Fact(DisplayName = "A rollback with no transaction open still discards the pending changes, and does not throw")]
    public async Task Rollback_without_a_transaction_discards_pending_changes()
    {
        await using var context = await CreatedContextAsync();
        var unitOfWork = new EfCoreUnitOfWork<OrdersDbContext>(context);
        context.Orders.Add(new Order { Text = "tracked only" });

        await unitOfWork.RollbackAsync(CancellationToken.None);

        context.ChangeTracker.Entries().Should().BeEmpty();
        (await context.SaveChangesAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact(DisplayName = "A transaction the application opened on the context counts as active, and a second one is refused")]
    public async Task Application_transaction_counts_as_active()
    {
        await using var context = await CreatedContextAsync();
        var unitOfWork = new EfCoreUnitOfWork<OrdersDbContext>(context);

        await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

        unitOfWork.HasActiveTransaction.Should().BeTrue();
        var act = () => unitOfWork.BeginTransactionAsync(IsolationLevel.Unspecified, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact(DisplayName = "An isolation level is passed to the relational transaction; Unspecified begins the provider's default")]
    public async Task Isolation_level_reaches_the_transaction()
    {
        await using var context = await CreatedContextAsync();
        var unitOfWork = new EfCoreUnitOfWork<OrdersDbContext>(context);

        await unitOfWork.BeginTransactionAsync(IsolationLevel.Serializable, CancellationToken.None);
        context.Database.CurrentTransaction!.GetDbTransaction().IsolationLevel.Should().Be(IsolationLevel.Serializable);
        await unitOfWork.RollbackAsync(CancellationToken.None);

        await unitOfWork.BeginTransactionAsync(IsolationLevel.Unspecified, CancellationToken.None);
        unitOfWork.HasActiveTransaction.Should().BeTrue();
        await unitOfWork.CommitAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "A context whose execution strategy retries on failure is refused with a message that names the remedy")]
    public async Task Retrying_execution_strategy_is_refused()
    {
        await (await CreatedContextAsync()).DisposeAsync();
        await using var context = NewContext(o => o.ExecutionStrategy(d => new RetryingStrategy(d)));
        var unitOfWork = new EfCoreUnitOfWork<OrdersDbContext>(context);

        var act = () => unitOfWork.BeginTransactionAsync(IsolationLevel.Unspecified, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("EnableRetryOnFailure").And.Contain("UseResilience");
        unitOfWork.HasActiveTransaction.Should().BeFalse();
    }

    [Fact(DisplayName = "Through the pipeline, a transactional command that publishes nothing still has its tracked changes committed")]
    public async Task Command_without_notifications_commits_its_changes()
    {
        await using var provider = await BuildAsync();
        await using (var scope = provider.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new PlaceOrder("quiet"), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();

        (await OrderCountAsync()).Should().Be(1);
    }

    [Fact(DisplayName = "Through the pipeline, a retried command whose first attempt wrote and then failed inserts its row once")]
    public async Task Retry_after_a_rollback_inserts_once()
    {
        await using var provider = await BuildAsync(b => b.UseResilience(o => o.BaseDelay = TimeSpan.Zero));
        provider.GetRequiredService<OrderProbe>().FailFirstAttempt = true;

        await using (var scope = provider.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Send(new PlaceOrder("retried"), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();

        provider.GetRequiredService<OrderProbe>().Runs.Should().Be(2);
        (await OrderCountAsync()).Should().Be(1, "the failed attempt's row was rolled back and cleared from the tracker, not saved by the retry");
    }

    [Fact(DisplayName = "Through the pipeline, a failed result's changes are not committed by the next transactional command in the scope")]
    public async Task Failed_result_is_not_committed_by_the_next_command()
    {
        await using var provider = await BuildAsync();
        await using (var scope = provider.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
            (await dispatcher.Send(new PlaceOrder("declined") { Decline = true }, TestContext.Current.CancellationToken)).IsSuccess.Should().BeFalse();
            (await dispatcher.Send(new PlaceOrder("accepted"), TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
        }

        await using var check = NewContext();
        (await check.Orders.Select(o => o.Text).ToListAsync(TestContext.Current.CancellationToken)).Should().Equal("accepted");
    }

    [Fact(DisplayName = "Transactional outbox over the same context: the messages commit with the rows, and a failed command leaves neither")]
    public async Task Outbox_messages_commit_with_the_rows()
    {
        await using var provider = await BuildAsync(outbox: o => o.Transactional().UseEntityFrameworkCore<OrdersDbContext>());
        await using (var scope = provider.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();
            (await dispatcher.Send(new PlaceOrder("announced") { Announce = true }, TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
            var failing = () => dispatcher.Send(new PlaceOrder("broken") { Announce = true, Throw = true }, TestContext.Current.CancellationToken);
            await failing.Should().ThrowAsync<InvalidOperationException>();
        }

        await using var check = NewContext();
        (await check.Orders.Select(o => o.Text).ToListAsync(TestContext.Current.CancellationToken)).Should().Equal("announced");
        (await check.Set<OutboxEntity>().CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        provider.GetRequiredService<OrderProbe>().Announced.Should().Be(0, "the notification went to the outbox, not in-process");
    }

    [Fact(DisplayName = "Transactional outbox: a publish inside a transaction the application opened on the context goes into that transaction")]
    public async Task Publish_inside_an_application_transaction_goes_into_it()
    {
        await using var provider = await BuildAsync(outbox: o => o.Transactional().UseEntityFrameworkCore<OrdersDbContext>());
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);

            await scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>().Publish(new OrderAnnounced("inside"), TestContext.Current.CancellationToken);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        await using var check = NewContext();
        (await check.Set<OutboxEntity>().CountAsync(TestContext.Current.CancellationToken)).Should().Be(0, "the message was written inside the rolled-back transaction");
        provider.GetRequiredService<OrderProbe>().Announced.Should().Be(0);
    }

    private async Task<ServiceProvider> BuildAsync(Action<ICqrsBuilder>? configure = null, Action<OutboxStoreBuilder>? outbox = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<OrderProbe>();
        services.AddDbContext<OrdersDbContext>(o => o.UseSqlite(ConnectionString));
        services.AddCqrsGenerated(b =>
        {
            b.UseEntityFrameworkCoreUnitOfWork<OrdersDbContext>();
            if (outbox is not null) b.UseOutbox(outbox);
            configure?.Invoke(b);
        });
        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Database.EnsureCreatedAsync();

        return provider;
    }

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    private sealed class RetryingStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(1))
    {
        protected override bool ShouldRetryOn(Exception exception) => false;
    }
}

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyCqrsOutbox();
        modelBuilder.Entity<Order>().HasKey(o => o.Id);
    }
}

public sealed class Order
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

public sealed class OrderProbe
{
    private int _runs;
    private int _announced;
    public int Runs => _runs;
    public int Announced => _announced;
    public bool FailFirstAttempt { get; set; }
    public int Run() => Interlocked.Increment(ref _runs);
    public void Announce() => Interlocked.Increment(ref _announced);
}

public sealed class PlaceOrder(string text) : CommandBase, ITransactionalCommand, IRetryableRequest
{
    public string Text { get; } = text;
    public bool Decline { get; init; }
    public bool Announce { get; init; }
    public bool Throw { get; init; }
    public IsolationLevel IsolationLevel => IsolationLevel.Unspecified;
}

// Adds its row and leaves it tracked: saving it is the unit of work's job.
public sealed class PlaceOrderHandler(OrdersDbContext context, OrderProbe probe, ICqrsDispatcher dispatcher) : ICommandHandler<PlaceOrder>
{
    public async Task<CommandResult> Handle(PlaceOrder command, CancellationToken cancellationToken)
    {
        var run = probe.Run();
        context.Orders.Add(new Order { Text = command.Text });
        if (command.Announce) await dispatcher.Publish(new OrderAnnounced(command.Text), cancellationToken);
        if (command.Throw || (probe.FailFirstAttempt && run == 1)) throw new InvalidOperationException("failed after writing");
        return command.Decline ? CommandResult.FromError("declined after writing") : CommandResult.FromSuccess();
    }
}

[NotificationName("tests.order.announced")]
public sealed record OrderAnnounced(string Text) : INotification;

public sealed class OrderAnnouncedHandler(OrderProbe probe) : INotificationHandler<OrderAnnounced>
{
    public Task Handle(OrderAnnounced notification, CancellationToken cancellationToken)
    {
        probe.Announce();
        return Task.CompletedTask;
    }
}
