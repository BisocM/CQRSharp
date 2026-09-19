using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Idempotency replay through the real wiring: <c>UseIdempotency(i =&gt; i.ReplayResultsWith(jsonOptions))</c>, the
///     in-memory store, the generated dispatch and System.Text.Json round-tripping a <see cref="CommandResult{TResult}" />.
/// </summary>
public sealed class IdempotencyReplayEndToEndTests
{
    [Fact(DisplayName = "A retried value-returning command gets the original value back and does not run twice")]
    public async Task Value_returning_command_is_replayed()
    {
        var json = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var services = new ServiceCollection();
        services.AddSingleton<ReceiptCounter>();
        services.AddCqrsGenerated(b => b.UseIdempotency(i => i.UseInMemoryStore().ReplayResultsWith(json)));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var first = await dispatcher.Send(new IssueReceipt { IdempotencyKey = "order-7" });
        var retry = await dispatcher.Send(new IssueReceipt { IdempotencyKey = "order-7" });

        first.IsSuccess.Should().BeTrue();
        first.Value.Should().Be("receipt-1");
        retry.IsSuccess.Should().BeTrue();
        retry.Value.Should().Be("receipt-1", "the retry is answered with the original receipt, not a new one");
        provider.GetRequiredService<ReceiptCounter>().Issued.Should().Be(1);
    }

    [Fact(DisplayName = "A failed result is not remembered: the retry runs again")]
    public async Task Failed_result_is_not_replayed()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ReceiptCounter>();
        services.AddCqrsGenerated(b => b.UseIdempotency());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var declined = await dispatcher.Send(new IssueReceipt { IdempotencyKey = "order-8", Decline = true });
        var retry = await dispatcher.Send(new IssueReceipt { IdempotencyKey = "order-8" });

        declined.IsSuccess.Should().BeFalse();
        retry.IsSuccess.Should().BeTrue("a declined attempt releases its claim, so the corrected retry is processed");
    }
}

public sealed class ReceiptCounter
{
    private int _issued;
    public int Issued => _issued;
    public int Next() => Interlocked.Increment(ref _issued);
}

public sealed class IssueReceipt : ResultCommandBase<string>, IIdempotentRequest
{
    public required string IdempotencyKey { get; init; }
    public bool Decline { get; init; }
}

public sealed class IssueReceiptHandler(IServiceProvider services) : IResultCommandHandler<IssueReceipt, string>
{
    public Task<CommandResult<string>> Handle(IssueReceipt command, CancellationToken cancellationToken)
    {
        if (command.Decline)
            return Task.FromResult(CommandResult<string>.FromError("declined"));

        // Optional, so the (assembly-wide auto-registered) handler is harmless in every other test's container.
        var number = services.GetService<ReceiptCounter>()?.Next() ?? 0;
        return Task.FromResult(CommandResult<string>.FromSuccess($"receipt-{number}"));
    }
}
