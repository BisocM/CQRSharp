using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore.Providers;

/// <summary>The partition claim race on a real database, where the claim's read and write are separate statements under read committed.</summary>
public abstract class ProviderOutboxPartitionClaimRaceTests(RelationalProviderFixture fixture) : OutboxPartitionClaimRaceTests<ProviderTestDbContext>
{
    protected override ProviderTestDbContext NewContext(params IInterceptor[] interceptors)
        => new(new DbContextOptionsBuilder<ProviderTestDbContext>(fixture.CreateOptions()).AddInterceptors(interceptors).Options);

    protected override Task ResetAsync()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        return fixture.ResetAsync();
    }
}

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlOutboxPartitionClaimRaceTests(PostgreSqlFixture fixture) : ProviderOutboxPartitionClaimRaceTests(fixture);

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerOutboxPartitionClaimRaceTests(SqlServerFixture fixture) : ProviderOutboxPartitionClaimRaceTests(fixture);
