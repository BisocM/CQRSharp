using System.Text.Json;
using System.Text.Json.Serialization;
using CQRSharp.Abstractions.Attributes.Notifications;
using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Core.Extensions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Round-trip tests for the source-generated outbox serializer beyond the original scalar-only surface:
///     the full primitive set, nullable values, enums, nested objects (mutable and constructor-based), and
///     collections (arrays, lists, nested arrays). Each notification below is picked up by the CQRSharp source
///     generator running on this test assembly, so a successful build already proves the shapes are supported
///     (an unsupported shape would raise CQRGEN005 as a build error).
/// </summary>
public sealed class OutboxSerializerComplexTypesTests
{
    // The generated serializer is now per-assembly and internal; resolve the composite INotificationSerializer (which
    // delegates to it) from DI rather than reflecting on a fixed generated type name.
    private static INotificationSerializer GetSerializer()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<INotificationSerializer>();
    }

    private static T RoundTrip<T>(T original) where T : class, INotification
    {
        var serializer = GetSerializer();
        var name = serializer.GetNotificationName(typeof(T));
        var payload = serializer.Serialize(original);
        payload.Should().NotBeNullOrEmpty();
        var result = serializer.Deserialize(name, payload);
        return result.Should().BeOfType<T>().Subject;
    }

    [Fact(DisplayName = "Outbox: full primitive + nullable + enum surface round-trips")]
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

    [Fact(DisplayName = "Outbox: nullable values serialized as null round-trip back to null")]
    public void Scalars_Nulls_RoundTrip()
    {
        var original = new ScalarsNotification { MaybeInt = null, MaybeColor = null, MaybeString = null };
        var result = RoundTrip(original);
        result.MaybeInt.Should().BeNull();
        result.MaybeColor.Should().BeNull();
        result.MaybeString.Should().BeNull();
    }

    [Fact(DisplayName = "Outbox: nested objects (including a null reference) round-trip")]
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

    [Fact(DisplayName = "Outbox: arrays, lists, nullable elements, and nested arrays round-trip")]
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

    [Fact(DisplayName = "Outbox: constructor-based objects, [JsonPropertyName], and [JsonIgnore] are honored")]
    public void ImmutableObjects_RoundTrip()
    {
        var original = new ImmutableNotification("abc", new Money(9.99m, "USD")) { Ignored = "should-not-persist" };

        var serializer = GetSerializer();
        var payload = serializer.Serialize(original);

        // [JsonPropertyName] renames the wire key; [JsonIgnore] drops the property entirely.
        var json = System.Text.Encoding.UTF8.GetString(payload);
        json.Should().Contain("\"identifier\"");
        json.Should().NotContain("should-not-persist");

        var result = (ImmutableNotification)serializer.Deserialize(
            serializer.GetNotificationName(typeof(ImmutableNotification)), payload)!;

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

    [NotificationName("outbox.complex.scalars")]
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
}
