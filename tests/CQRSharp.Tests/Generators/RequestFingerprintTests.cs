using System.Text.Json.Serialization;
using CQRSharp.Core.Idempotency;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Generators;

/// <summary>
///     The source-generated payload fingerprints of this test assembly's idempotent requests: equal payloads hash alike
///     however their dictionaries and sets were built, and every member that is payload changes the hash.
/// </summary>
public sealed class RequestFingerprintTests
{
    private static string Fingerprint(IRequest request)
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        var fingerprinter = services.BuildServiceProvider().GetRequiredService<IRequestFingerprinter>();
        fingerprinter.TryFingerprint(request, out var fingerprint).Should().BeTrue();
        return fingerprint;
    }

    [Fact(DisplayName = "Dictionaries and sets built in a different order fingerprint alike; different content does not")]
    public void Keyed_payloads_are_canonical()
    {
        var first = new FingerprintedOrder
        {
            Lines = new Dictionary<string, decimal> { ["b"] = 2m, ["a"] = 1m },
            Codes = [3, 1, 2],
            Labels = new HashSet<Label> { new() { Name = "y" }, new() { Name = "x" } }
        };
        var reordered = new FingerprintedOrder
        {
            Lines = new Dictionary<string, decimal> { ["a"] = 1m, ["b"] = 2m },
            Codes = [2, 3, 1],
            Labels = new HashSet<Label> { new() { Name = "x" }, new() { Name = "y" } }
        };
        var changed = new FingerprintedOrder
        {
            Lines = new Dictionary<string, decimal> { ["a"] = 1m, ["b"] = 3m },
            Codes = [2, 3, 1],
            Labels = new HashSet<Label> { new() { Name = "x" }, new() { Name = "y" } }
        };

        Fingerprint(reordered).Should().Be(Fingerprint(first), "a retry of the same payload must not read as a mismatch");
        Fingerprint(changed).Should().NotBe(Fingerprint(first));
    }

    [Fact(DisplayName = "A DateOnly, TimeOnly or char member is part of the payload fingerprint")]
    public void Date_and_time_members_are_fingerprinted()
    {
        static BookRoom Booking(int day = 1, int hour = 9, char grade = 'A')
            => new() { Day = new DateOnly(2024, 5, day), At = new TimeOnly(hour, 0), Grade = grade };

        var booked = Fingerprint(Booking());

        Fingerprint(Booking(day: 2)).Should().NotBe(booked);
        Fingerprint(Booking(hour: 10)).Should().NotBe(booked);
        Fingerprint(Booking(grade: 'B')).Should().NotBe(booked);
        Fingerprint(Booking()).Should().Be(booked);
    }

    [Fact(DisplayName = "A member behind a conditional [JsonIgnore] is payload; one behind an unconditional one is not")]
    public void Conditional_json_ignore_is_fingerprinted()
    {
        var charge = Fingerprint(new ConditionalCharge { Coupon = "A", Note = "n" });

        Fingerprint(new ConditionalCharge { Coupon = "B", Note = "n" }).Should().NotBe(charge);
        Fingerprint(new ConditionalCharge { Coupon = "A", Note = "other" }).Should().Be(charge);
    }

    public sealed record Label
    {
        public string Name { get; init; } = "";
    }

    public sealed class FingerprintedOrder : CommandBase, IIdempotentRequest
    {
        public string IdempotencyKey { get; init; } = "order";
        public Dictionary<string, decimal> Lines { get; init; } = [];
        public HashSet<int> Codes { get; init; } = [];
        public ISet<Label> Labels { get; init; } = new HashSet<Label>();
    }

    public sealed class FingerprintedOrderHandler : ICommandHandler<FingerprintedOrder>
    {
        public Task<CommandResult> Handle(FingerprintedOrder command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
    }

    public sealed class BookRoom : CommandBase, IIdempotentRequest
    {
        public string IdempotencyKey { get; init; } = "room";
        public DateOnly Day { get; init; }
        public TimeOnly At { get; init; }
        public char Grade { get; init; }
    }

    public sealed class BookRoomHandler : ICommandHandler<BookRoom>
    {
        public Task<CommandResult> Handle(BookRoom command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
    }

    public sealed class ConditionalCharge : CommandBase, IIdempotentRequest
    {
        public string IdempotencyKey { get; init; } = "charge";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Coupon { get; init; }

        [JsonIgnore]
        public string Note { get; init; } = "";
    }

    public sealed class ConditionalChargeHandler : ICommandHandler<ConditionalCharge>
    {
        public Task<CommandResult> Handle(ConditionalCharge command, CancellationToken cancellationToken) => Task.FromResult(CommandResult.FromSuccess());
    }
}
