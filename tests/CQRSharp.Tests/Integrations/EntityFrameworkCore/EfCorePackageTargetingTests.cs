using CQRSharp.EntityFrameworkCore;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CQRSharp.Tests.Integrations.EntityFrameworkCore;

// EF Core 9 moved ExecuteUpdate/ExecuteDelete to another assembly and EF Core 10 changed ExecuteUpdate's signature, so IL
// compiled against one EF Core major throws MissingMethodException on the next - on every outbox claim and every
// idempotency claim. Each target framework of the package is therefore built against its own framework's major, and the
// store tests of each framework run on that major.
public sealed class EfCorePackageTargetingTests
{
    [Fact(DisplayName = "CQRSharp.EntityFrameworkCore is compiled against the EF Core major of its target framework")]
    public void Package_binds_to_the_frameworks_ef_core_major()
    {
        var efCore = typeof(EfCoreOutboxStore<>).Assembly.GetReferencedAssemblies()
            .Single(a => a.Name == "Microsoft.EntityFrameworkCore");

        efCore.Version!.Major.Should().Be(Environment.Version.Major);
    }

    [Fact(DisplayName = "The EF Core store tests run on the EF Core major of the framework they target")]
    public void Tests_run_on_the_frameworks_ef_core_major()
    {
        typeof(DbContext).Assembly.GetName().Version!.Major.Should().Be(Environment.Version.Major);
    }
}
