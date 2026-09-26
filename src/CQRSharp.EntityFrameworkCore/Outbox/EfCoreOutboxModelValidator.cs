using CQRSharp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     Fails host start when <typeparamref name="TContext" /> does not map the outbox and inbox tables. Without it a
///     context that maps only the outbox cannot hold a message, and an unmapped inbox fails every delivery's check
///     before its handler runs. Skipped once a later registration replaced this store: its context is not used.
/// </summary>
internal sealed class EfCoreOutboxModelValidator<TContext>(IServiceScopeFactory scopeFactory, EfCoreStoreSelection selection)
    : IValidateOptions<EfCoreOutboxStoreOptions>
    where TContext : DbContext
{
    public ValidateOptionsResult Validate(string? name, EfCoreOutboxStoreOptions options)
    {
        if (!selection.Selects<IOutboxStore, EfCoreOutboxStore<TContext>>()) return ValidateOptionsResult.Skip;

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetService<TContext>();
        if (context is null)
            return ValidateOptionsResult.Fail(
                $"{typeof(TContext).Name} is not registered. Call AddDbContext<{typeof(TContext).Name}>() before using it as the EF Core outbox store.");

        var model = context.Model;
        var missing = new List<string>(2);
        if (model.FindEntityType(typeof(OutboxEntity)) is null) missing.Add(nameof(OutboxEntity));
        if (model.FindEntityType(typeof(InboxEntity)) is null) missing.Add(nameof(InboxEntity));

        return missing.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{typeof(TContext).Name} does not map {string.Join(" and ", missing)}. Call modelBuilder.ApplyCqrsOutbox() in OnModelCreating: it maps the outbox table and the inbox table the processor records deliveries in.");
    }
}
