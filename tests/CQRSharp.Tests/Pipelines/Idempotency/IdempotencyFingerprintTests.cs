using CQRSharp.Core.Idempotency;
using CQRSharp.Tests.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     Idempotency-key reuse with a different payload, end to end: the generated fingerprinter, the in-memory store and
///     the behavior. A retry with the same payload is replayed; a different payload under the same key is rejected.
/// </summary>
public sealed class IdempotencyFingerprintTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<FingerprintCounter>();
        services.AddCqrsGenerated(b => b.UseIdempotency());
        return services.BuildServiceProvider();
    }

    [Fact(DisplayName = "The generated fingerprinter hashes every idempotent request's payload, ignoring the request infrastructure")]
    public void Generated_fingerprints_are_stable_and_payload_only()
    {
        using var provider = Build();
        var fingerprinter = provider.GetRequiredService<IRequestFingerprinter>();

        fingerprinter.TryFingerprint(new ReserveSeat("show-1", 7) { IdempotencyKey = "k" }, out var first).Should().BeTrue();
        fingerprinter.TryFingerprint(new ReserveSeat("show-1", 7) { IdempotencyKey = "k" }.WithContext(new RequestContextBase()), out var same).Should().BeTrue();
        fingerprinter.TryFingerprint(new ReserveSeat("show-1", 8) { IdempotencyKey = "k" }, out var other).Should().BeTrue();

        first.Should().HaveLength(64, "a SHA-256 rendered as hex");
        same.Should().Be(first, "the dispatcher's own request state is not payload");
        other.Should().NotBe(first);
        fingerprinter.TryFingerprint(new CountFingerprints(), out _).Should().BeFalse("a request that is not idempotent has no fingerprint");
    }

    [Fact(DisplayName = "The same key with a different payload is rejected with IdempotencyKeyMismatchException; the same payload is replayed")]
    public async Task Different_payload_under_the_same_key_is_rejected()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        (await dispatcher.Send(new ReserveSeat("show-1", 7) { IdempotencyKey = "booking-1" }, TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();
        (await dispatcher.Send(new ReserveSeat("show-1", 7) { IdempotencyKey = "booking-1" }, TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue("the retry is replayed");

        var reused = () => dispatcher.Send(new ReserveSeat("show-1", 8) { IdempotencyKey = "booking-1" });
        (await reused.Should().ThrowAsync<IdempotencyKeyMismatchException>()).Which.IdempotencyKey.Should().Be("booking-1");
        provider.GetRequiredService<FingerprintCounter>().Reservations.Should().Be(1);
    }

    [Fact(DisplayName = "A request that fingerprints itself decides what counts as the payload")]
    public async Task Self_fingerprinted_requests_control_the_comparison()
    {
        await using var provider = Build();
        await using var scope = provider.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<ICqrsDispatcher>();

        var requestedAt = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        await dispatcher.Send(new TimestampedTransfer(100, requestedAt) { IdempotencyKey = "transfer-1" }, TestContext.Current.CancellationToken);
        // A different timestamp is still the same transfer: the request excludes it from its own fingerprint.
        (await dispatcher.Send(new TimestampedTransfer(100, requestedAt.AddMinutes(1)) { IdempotencyKey = "transfer-1" }, TestContext.Current.CancellationToken)).IsSuccess.Should().BeTrue();

        var different = () => dispatcher.Send(new TimestampedTransfer(250, requestedAt) { IdempotencyKey = "transfer-1" });
        await different.Should().ThrowAsync<IdempotencyKeyMismatchException>();
    }
}

public sealed class FingerprintCounter
{
    private int _reservations;
    public int Reservations => _reservations;
    public void Reserved() => Interlocked.Increment(ref _reservations);
}

public sealed class ReserveSeat(string show, int seat) : CommandBase, IIdempotentRequest
{
    public string Show { get; } = show;
    public int Seat { get; } = seat;
    public required string IdempotencyKey { get; init; }
}

public sealed class ReserveSeatHandler(IServiceProvider services) : ICommandHandler<ReserveSeat>
{
    public Task<CommandResult> Handle(ReserveSeat command, CancellationToken cancellationToken)
    {
        services.GetService<FingerprintCounter>()?.Reserved();
        return Task.FromResult(CommandResult.FromSuccess());
    }
}

public sealed class TimestampedTransfer(decimal amount, DateTime requestedAt) : CommandBase, IFingerprintedRequest
{
    public decimal Amount { get; } = amount;
    public DateTime RequestedAt { get; } = requestedAt;
    public required string IdempotencyKey { get; init; }
    public string Fingerprint => FormattableString.Invariant($"amount:{Amount}");
}

public sealed class TimestampedTransferHandler : ICommandHandler<TimestampedTransfer>
{
    public Task<CommandResult> Handle(TimestampedTransfer command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}

public sealed class CountFingerprints : CommandBase;

public sealed class CountFingerprintsHandler : ICommandHandler<CountFingerprints>
{
    public Task<CommandResult> Handle(CountFingerprints command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
}
