using System.Text.Json;
using System.Text.Json.Serialization;
using CQRSharp.Persistence;
using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The source-generated outbox serializer. The CQRSharp source generator runs on this test assembly and emits the
///     serializer into its module namespace, covering every notification here that carries a
///     <see cref="NotificationNameAttribute" />; the tests resolve it from the container as
///     <see cref="INotificationSerializer" />. A shape the generator could not serialize would fail the build
///     (CQRGEN005), so these tests pin what each supported shape round-trips to, and how names and unreadable
///     payloads are answered.
/// </summary>
public sealed class OutboxSerializerTests : IDisposable
{
    private const string ScalarsName = "outbox.complex.scalars";

    private readonly ServiceProvider _provider = new ServiceCollection().AddCqrsGenerated().BuildServiceProvider();
    private readonly INotificationSerializer _serializer;

    public OutboxSerializerTests() => _serializer = _provider.GetRequiredService<INotificationSerializer>();

    public void Dispose() => _provider.Dispose();

    private T RoundTrip<T>(T original) where T : class, INotification
    {
        _serializer.TryGetNotificationName(typeof(T), out var name).Should().BeTrue();
        var payload = _serializer.Serialize(original);
        payload.Should().NotBeNullOrEmpty();
        var result = _serializer.Deserialize(name!, payload);
        return result.Should().BeOfType<T>().Subject;
    }

    [Fact(DisplayName = "Outbox serializer: a notification's name is the stable name its [NotificationName] declares")]
    public void The_name_is_the_declared_stable_name()
    {
        _serializer.TryGetNotificationName(typeof(ScalarsNotification), out var name).Should().BeTrue();
        name.Should().Be(ScalarsName);
    }

    [Fact(DisplayName = "Outbox serializer: a notification without [NotificationName] gets no name, so it is not durable")]
    public void A_notification_without_a_name_is_not_durable()
    {
        _serializer.TryGetNotificationName(typeof(UnnamedNotification), out var name).Should().BeFalse();
        name.Should().BeNull();
    }

    [Fact(DisplayName = "Outbox serializer: a type derived from a named notification is not covered by the base type's name")]
    public void The_name_matches_the_exact_type()
    {
        _serializer.TryGetNotificationName(typeof(DerivedFromNamedNotification), out _).Should().BeFalse(
            "a derived notification has a payload shape of its own, which the base type's serializer would not write");
    }

    [Fact(DisplayName = "Outbox serializer: an unknown notification name deserializes to null, even with a valid payload")]
    public void An_unknown_name_deserializes_to_null()
    {
        var validPayload = _serializer.Serialize(new ScalarsNotification { I = 1 });

        // Null tells the processor the name is unknown here, which it treats differently from a corrupt payload.
        _serializer.Deserialize("totally.unknown.name", validPayload).Should().BeNull();
    }

    [Fact(DisplayName = "Outbox serializer: a corrupt payload for a known name throws JsonException")]
    public void A_corrupt_payload_for_a_known_name_throws()
    {
        var act = () => _serializer.Deserialize(ScalarsName, "this is not json {"u8.ToArray());

        act.Should().Throw<JsonException>("a corrupt payload must not collapse into an unknown name");
    }

    [Fact(DisplayName = "Outbox serializer: an empty payload for a known name is corrupt (JsonException), not an unknown name")]
    public void An_empty_payload_for_a_known_name_throws()
    {
        var act = () => _serializer.Deserialize(ScalarsName, Array.Empty<byte>());

        act.Should().Throw<JsonException>();
    }

    [Fact(DisplayName = "Outbox serializer: the full primitive + nullable + enum surface round-trips")]
    public void Scalars_RoundTrip()
    {
        var original = new ScalarsNotification
        {
            B = 200, Sb = -100, S = -30000, Us = 60000,
            I = -123456, Ui = 4000000000, L = -9000000000000, Ul = ulong.MaxValue,
            F = 1.5f, D = 2.5, Dec = 12345.6789m, Bo = true,
            G = Guid.NewGuid(),
            Dt = new DateTime(2021, 5, 4, 3, 2, 1, DateTimeKind.Utc),
            Dto = new DateTimeOffset(2021, 5, 4, 3, 2, 1, TimeSpan.FromHours(2)),
            Ts = new TimeSpan(0, 1, 30, 0),
            Col = Color.Blue,
            MaybeInt = 7,
            MaybeColor = Color.Green,
            MaybeString = "present"
        };

        RoundTrip(original).Should().BeEquivalentTo(original);
    }

    [Fact(DisplayName = "Outbox serializer: nullable values serialized as null round-trip back to null")]
    public void Scalars_Nulls_RoundTrip()
    {
        var original = new ScalarsNotification { MaybeInt = null, MaybeColor = null, MaybeString = null };
        var result = RoundTrip(original);
        result.MaybeInt.Should().BeNull();
        result.MaybeColor.Should().BeNull();
        result.MaybeString.Should().BeNull();
    }

    [Fact(DisplayName = "Outbox serializer: nested objects (including a null reference) round-trip")]
    public void NestedObjects_RoundTrip()
    {
        var original = new NestedNotification
        {
            Title = "Order #1",
            Ship = new Address { Street = "1 Main St", City = "Springfield", Zip = 12345 },
            Bill = null
        };

        var result = RoundTrip(original);
        result.Should().BeEquivalentTo(original);
        result.Bill.Should().BeNull();
    }

    [Fact(DisplayName = "Outbox serializer: arrays, lists, nullable elements, and nested arrays round-trip")]
    public void Collections_RoundTrip()
    {
        var original = new CollectionsNotification
        {
            Ints = new[] { 1, 2, 3 },
            Tags = new List<string> { "a", "b" },
            Addresses = new List<Address>
            {
                new() { Street = "1 A St", City = "X", Zip = 1 },
                new() { Street = "2 B St", City = "Y", Zip = 2 }
            },
            MaybeInts = new List<int?> { 1, null, 3 },
            Matrix = new[] { new[] { "a", "b" }, new[] { "c" } }
        };

        RoundTrip(original).Should().BeEquivalentTo(original);
    }

    [Fact(DisplayName = "Outbox serializer: constructor-based objects, [JsonPropertyName], and [JsonIgnore] are honored")]
    public void ImmutableObjects_RoundTrip()
    {
        var original = new ImmutableNotification("abc", new Money(9.99m, "USD")) { Ignored = "should-not-persist" };

        var payload = _serializer.Serialize(original);

        // [JsonPropertyName] renames the wire key; [JsonIgnore] drops the property entirely.
        var json = System.Text.Encoding.UTF8.GetString(payload);
        json.Should().Contain("\"identifier\"");
        json.Should().NotContain("should-not-persist");

        _serializer.TryGetNotificationName(typeof(ImmutableNotification), out var name).Should().BeTrue();
        var result = (ImmutableNotification)_serializer.Deserialize(name!, payload)!;

        result.Id.Should().Be("abc");
        result.Amount.Value.Should().Be(9.99m);
        result.Amount.Currency.Should().Be("USD");
        result.Ignored.Should().Be("default", "the [JsonIgnore] property must not be persisted or restored");
    }

    public enum Color
    {
        Red = 0,
        Green = 5,
        Blue = 9
    }

    [NotificationName(ScalarsName)]
    public sealed class ScalarsNotification : INotification
    {
        public byte B { get; set; }
        public sbyte Sb { get; set; }
        public short S { get; set; }
        public ushort Us { get; set; }
        public int I { get; set; }
        public uint Ui { get; set; }
        public long L { get; set; }
        public ulong Ul { get; set; }
        public float F { get; set; }
        public double D { get; set; }
        public decimal Dec { get; set; }
        public bool Bo { get; set; }
        public Guid G { get; set; }
        public DateTime Dt { get; set; }
        public DateTimeOffset Dto { get; set; }
        public TimeSpan Ts { get; set; }
        public Color Col { get; set; }
        public int? MaybeInt { get; set; }
        public Color? MaybeColor { get; set; }
        public string? MaybeString { get; set; }
    }

    [NotificationName("outbox.complex.nested")]
    public sealed class NestedNotification : INotification
    {
        public string Title { get; set; } = string.Empty;
        public Address Ship { get; set; } = new();
        public Address? Bill { get; set; }
    }

    [NotificationName("outbox.complex.collections")]
    public sealed class CollectionsNotification : INotification
    {
        public int[] Ints { get; set; } = Array.Empty<int>();
        public List<string> Tags { get; set; } = new();
        public IReadOnlyList<Address> Addresses { get; set; } = new List<Address>();
        public List<int?> MaybeInts { get; set; } = new();
        public string[][] Matrix { get; set; } = Array.Empty<string[]>();
    }

    public sealed class Address
    {
        public string Street { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public int Zip { get; set; }
    }

    [NotificationName("outbox.complex.immutable")]
    public sealed class ImmutableNotification : INotification
    {
        public ImmutableNotification(string id, Money amount)
        {
            Id = id;
            Amount = amount;
        }

        [JsonPropertyName("identifier")]
        public string Id { get; }

        public Money Amount { get; }

        [JsonIgnore]
        public string Ignored { get; set; } = "default";
    }

    public sealed class Money
    {
        public Money(decimal value, string currency)
        {
            Value = value;
            Currency = currency;
        }

        public decimal Value { get; }
        public string Currency { get; }
    }

    public sealed record UnnamedNotification : INotification;

    [NotificationName("outbox.serializer.named-base")]
    public class NamedBaseNotification : INotification;

    public sealed class DerivedFromNamedNotification : NamedBaseNotification;
}
