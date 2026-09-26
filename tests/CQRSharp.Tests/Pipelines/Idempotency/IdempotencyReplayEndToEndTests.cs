using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CQRSharp.Persistence;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Idempotency replay through the real wiring: <c>UseIdempotency(i =&gt; i.ReplayResultsWith(...))</c> with JSON
///     options or a serializer of the application's own, the in-memory store (chosen by <c>UseInMemoryStore()</c> or
///     <c>AddInMemoryIdempotencyStore()</c>), and the generated dispatch round-tripping a
///     <see cref="CommandResult{TResult}" />.
/// </summary>
public sealed class IdempotencyReplayEndToEndTests
{
    [Theory(DisplayName = "A retried value-returning command gets the original value back and does not run twice, whichever verb chose the in-memory store")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Value_returning_command_is_replayed(bool builderVerb)
    {
        var json = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var services = new ServiceCollection();
        services.AddSingleton<ReceiptCounter>();
        if (builderVerb)
        {
            services.AddCqrsGenerated(b => b.UseIdempotency(i => i.UseInMemoryStore().ReplayResultsWith(json)));
        }
        else
        {
            services.AddInMemoryIdempotencyStore();
            services.AddCqrsGenerated(b => b.UseIdempotency(i => i.ReplayResultsWith(json)));
        }

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var first = await dispatcher.Send(new IssueReceipt { IdempotencyKey = "order-7" }, TestContext.Current.CancellationToken);
        var retry = await dispatcher.Send(new IssueReceipt { IdempotencyKey = "order-7" }, TestContext.Current.CancellationToken);

        first.IsSuccess.Should().BeTrue();
        first.Value.Should().Be("receipt-1");
        retry.IsSuccess.Should().BeTrue();
        retry.Value.Should().Be("receipt-1", "the retry is answered with the original receipt, not a new one");
        provider.GetRequiredService<ReceiptCounter>().Issued.Should().Be(1);
    }

    [Fact(DisplayName = "A result serializer chosen with ReplayResultsWith(serializer) stores the result and answers the retry with it")]
    public async Task Custom_result_serializer_answers_the_retry()
    {
        var serializer = new ReceiptSerializer();
        var services = new ServiceCollection();
        services.AddSingleton<ReceiptCounter>();
        services.AddCqrsGenerated(b => b.UseIdempotency(i => i.UseInMemoryStore().ReplayResultsWith(serializer)));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var first = await dispatcher.Send(new IssueReceipt { IdempotencyKey = "order-9" }, TestContext.Current.CancellationToken);
        var retry = await dispatcher.Send(new IssueReceipt { IdempotencyKey = "order-9" }, TestContext.Current.CancellationToken);

        retry.Value.Should().Be(first.Value, "the retry is answered with the result the serializer stored");
        serializer.Calls.Should().Equal("serialize", "deserialize");
        provider.GetRequiredService<ReceiptCounter>().Issued.Should().Be(1);
    }

    [Theory(DisplayName = "Replay options that have no TypeInfoResolver, and so could resolve no result type, are rejected where they are configured")]
    [InlineData(JsonSerializerDefaults.General)]
    [InlineData(JsonSerializerDefaults.Web)]
    public void Options_without_a_type_info_resolver_are_rejected(JsonSerializerDefaults defaults)
    {
        var act = () => new CQRSharp.Pipelines.IdempotencyStoreBuilder().ReplayResultsWith(new JsonSerializerOptions(defaults));

        act.Should().Throw<ArgumentException>().WithMessage("*TypeInfoResolver*").Which.ParamName.Should().Be("options");
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

        var declined = await dispatcher.Send(new IssueReceipt { IdempotencyKey = "order-8", Decline = true }, TestContext.Current.CancellationToken);
        var retry = await dispatcher.Send(new IssueReceipt { IdempotencyKey = "order-8" }, TestContext.Current.CancellationToken);

        declined.IsSuccess.Should().BeFalse();
        retry.IsSuccess.Should().BeTrue("a declined attempt releases its claim, so the corrected retry is processed");
    }

    // Stores a receipt as its text, and records which way it was used.
    private sealed class ReceiptSerializer : IIdempotencyResultSerializer
    {
        public List<string> Calls { get; } = [];

        public bool TrySerialize<TResult>(TResult result, out byte[] payload)
        {
            Calls.Add("serialize");
            payload = result is CommandResult<string> { IsSuccess: true } receipt ? Encoding.UTF8.GetBytes(receipt.Value!) : [];
            return payload.Length > 0;
        }

        public bool TryDeserialize<TResult>(byte[] payload, [MaybeNullWhen(false)] out TResult result)
        {
            Calls.Add("deserialize");
            if (typeof(TResult) != typeof(CommandResult<string>))
            {
                result = default;
                return false;
            }

            result = (TResult)(object)CommandResult<string>.FromSuccess(Encoding.UTF8.GetString(payload));
            return true;
        }
    }
}
