using CQRSharp.Abstractions.Interfaces.Context;
using CQRSharp.Abstractions.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Models.Requests;
using CQRSharp.Core.Factories;
using FluentAssertions;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Verifies the async context-factory contract (4.1.0): an existing synchronous factory keeps working through the
///     defaulted <c>CreateContextAsync</c>, and an <see cref="AsyncRequestContextFactory{TContext}" /> routes the
///     executor's non-generic async call to its typed override. The dispatcher calls <c>CreateContextAsync</c> at the
///     awaited context-init point (see PipelineExecutor.InitializeRequestContextAsync), so the context is hydrated
///     before the handler runs.
/// </summary>
public sealed class AsyncContextFactoryTests
{
    [Fact]
    public async Task Default_CreateContextAsync_wraps_a_synchronous_factory()
    {
        IInternalRequestContextFactory factory = new SyncOnlyFactory();

        var context = await factory.CreateContextAsync(new ProbeRequest(), CancellationToken.None);

        context.Should().BeOfType<SyncProbeContext>().Which.Loaded.Should().Be("sync");
    }

    [Fact]
    public async Task AsyncRequestContextFactory_routes_the_non_generic_call_to_the_typed_async_override()
    {
        // The executor holds the factory as the non-generic IInternalRequestContextFactory and awaits CreateContextAsync;
        // this asserts that call reaches the typed, genuinely-async override and returns the hydrated context.
        IInternalRequestContextFactory factory = new AsyncTestContextFactory();

        var context = await factory.CreateContextAsync(new ProbeRequest(), CancellationToken.None);

        context.Should().BeOfType<AsyncTestContext>().Which.Loaded.Should().Be("hydrated-async");
    }

    [Fact]
    public void AsyncRequestContextFactory_synchronous_creation_is_not_supported_by_default()
    {
        var factory = new AsyncTestContextFactory();

        var act = () => factory.CreateContext(new ProbeRequest());

        act.Should().Throw<NotSupportedException>().WithMessage("*CreateContextAsync*");
    }

    private sealed class ProbeRequest : IRequest
    {
        public IRequestContext? Context { get; set; }
        public RequestMetadata? Metadata { get; set; }
    }
}

// A discovered factory whose context type no request uses — harmless to global discovery (no binding, so no validation).
public sealed class AsyncTestContext : RequestContextBase
{
    public string Loaded { get; set; } = "";
}

public sealed class AsyncTestContextFactory : AsyncRequestContextFactory<AsyncTestContext>
{
    public override async ValueTask<AsyncTestContext> CreateContextAsync(IRequest request, CancellationToken cancellationToken)
    {
        await Task.Delay(1, cancellationToken).ConfigureAwait(false); // a genuine async hop
        return new AsyncTestContext { Loaded = "hydrated-async" };
    }
}

public sealed class SyncProbeContext : RequestContextBase
{
    public string Loaded { get; set; } = "";
}

public sealed class SyncOnlyFactory : IRequestContextFactory<SyncProbeContext>
{
    public SyncProbeContext CreateContext(IRequest request) => new() { Loaded = "sync" };
}
