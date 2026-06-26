using CQRSharp.Abstractions.Interfaces.Outbox;
using CQRSharp.Core.Background.Outbox.Types;
using CQRSharp.Core.Options;
using CQRSharp.Testing.Outbox;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Runs the shared <see cref="OutboxStoreContractTests" /> conformance suite against the in-process
///     <c>InMemoryOutboxStore</c> shipped in CQRSharp.Core.
/// </summary>
public sealed class InMemoryOutboxStoreContractTests : OutboxStoreContractTests
{
    protected override FakeTimeProvider Time { get; } = new();

    protected override Task<IOutboxStore> CreateStoreAsync()
        => Task.FromResult<IOutboxStore>(new InMemoryOutboxStore(
            Time,
            Options.Create(new InMemoryOutboxStoreOptions { VisibilityTimeout = VisibilityTimeout })));
}
