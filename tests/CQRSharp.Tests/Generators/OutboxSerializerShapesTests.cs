using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CQRSharp.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Tests.Generators;

/// <summary>
///     The source-generated outbox serializer of this test assembly, for the shapes System.Text.Json also handles:
///     every notification here is serialized by generated code, and the payloads are checked against what
///     System.Text.Json writes and reads for the same types, including payloads stored before a member was added.
/// </summary>
public sealed class OutboxSerializerShapesTests
{
    private static readonly JsonSerializerOptions Stj = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static INotificationSerializer Serializer()
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated();
        return services.BuildServiceProvider().GetRequiredService<INotificationSerializer>();
    }

    private static T RoundTrip<T>(T original) where T : class, INotification
    {
        var serializer = Serializer();
        serializer.TryGetNotificationName(typeof(T), out var name).Should().BeTrue();
        return serializer.Deserialize(name!, serializer.Serialize(original)).Should().BeOfType<T>().Subject;
    }

    private static T Read<T>(string json) where T : class, INotification
    {
        var serializer = Serializer();
        serializer.TryGetNotificationName(typeof(T), out var name).Should().BeTrue();
        return serializer.Deserialize(name!, Encoding.UTF8.GetBytes(json)).Should().BeOfType<T>().Subject;
    }

    [Fact(DisplayName = "char, DateOnly, TimeOnly, TimeSpan and Uri round-trip, nullable or not")]
    public void Bcl_scalars_round_trip()
    {
        var original = BclScalars.Sample();

        RoundTrip(original).Should().BeEquivalentTo(original);
        RoundTrip(new BclScalars { Link = new Uri("relative/path", UriKind.Relative) }).Should().BeEquivalentTo(
            new BclScalars { Link = new Uri("relative/path", UriKind.Relative) });
    }

    [Fact(DisplayName = "The scalars are written as System.Text.Json writes them, and each reads the other's payload")]
    public void Bcl_scalars_are_wire_compatible_with_System_Text_Json()
    {
        var original = BclScalars.Sample();
        var generated = Serializer().Serialize(original);

        JsonSerializer.Deserialize<BclScalars>(generated, Stj).Should().BeEquivalentTo(original);
        Read<BclScalars>(JsonSerializer.Serialize(original, Stj)).Should().BeEquivalentTo(original);

        var json = Encoding.UTF8.GetString(generated);
        json.Should().Contain("\"grade\":\"B\"").And.Contain("\"day\":\"2024-02-29\"").And.Contain("\"at\":\"13:45:30.1250000\"")
            .And.Contain("\"took\":\"1.02:03:04\"").And.Contain("\"link\":\"https://example.com/a?b=c\"");
    }

    [Fact(DisplayName = "Sets and string-keyed dictionaries round-trip, nested values and null values included")]
    public void Sets_and_dictionaries_round_trip()
    {
        var original = Keyed.Sample();

        var result = RoundTrip(original);

        result.Should().BeEquivalentTo(original);
        result.Tags.Should().BeOfType<HashSet<string>>();
        result.Codes.Should().BeOfType<HashSet<int>>();
        result.Counts.Should().BeOfType<Dictionary<string, int>>();
    }

    [Fact(DisplayName = "An IReadOnlySet round-trips, read back as a HashSet")]
    public void Read_only_set_round_trips()
    {
        var original = new ReadOnlySetNotification { Ids = new HashSet<Guid> { Guid.NewGuid(), Guid.NewGuid() } };

        RoundTrip(original).Ids.Should().BeOfType<HashSet<Guid>>().Which.Should().BeEquivalentTo(original.Ids);
    }

    [Fact(DisplayName = "Collections of the same element type that differ in whether an element may be null each read nulls their own way")]
    public void Element_nullability_is_kept_apart()
    {
        var result = RoundTrip(new ElementNullability { Maybe = ["a", null], Never = ["b"] });

        result.Maybe.Should().Equal("a", null);
        result.Never.Should().Equal("b");
    }

    [Fact(DisplayName = "Sets and dictionaries are wire-compatible with System.Text.Json in both directions")]
    public void Sets_and_dictionaries_are_wire_compatible_with_System_Text_Json()
    {
        var original = Keyed.Sample();

        JsonSerializer.Deserialize<Keyed>(Serializer().Serialize(original), Stj).Should().BeEquivalentTo(original);
        Read<Keyed>(JsonSerializer.Serialize(original, Stj)).Should().BeEquivalentTo(original);
    }

    [Fact(DisplayName = "A malformed value in a payload is a JsonException, as System.Text.Json reports it")]
    public void Malformed_values_are_json_exceptions()
    {
        var serializer = Serializer();
        serializer.TryGetNotificationName(typeof(BclScalars), out var name).Should().BeTrue();

        foreach (var json in new[] { """{"grade":"AB"}""", """{"day":"29/02/2024"}""", """{"at":"1.00:00:00"}""", """{"took":5}""", """{"link":7}""" })
        {
            var read = () => serializer.Deserialize(name!, Encoding.UTF8.GetBytes(json));
            read.Should().Throw<JsonException>(json);
        }

        serializer.TryGetNotificationName(typeof(Keyed), out var keyed).Should().BeTrue();
        var readKeyed = () => serializer.Deserialize(keyed!, """{"counts":[1]}"""u8.ToArray());
        readKeyed.Should().Throw<JsonException>("a dictionary is a JSON object");
    }

    [Fact(DisplayName = "[JsonConstructor] picks the constructor when two could be used")]
    public void Json_constructor_is_honoured()
    {
        var result = RoundTrip(new MarkedConstructor("a", 1));

        result.Name.Should().Be("a");
        result.Count.Should().Be(1);
        result.ViaMarkedConstructor.Should().BeTrue();
    }

    [Fact(DisplayName = "Only an unconditional [JsonIgnore] leaves a member out; a conditional one still round-trips")]
    public void Conditional_json_ignore_round_trips()
    {
        var original = new IgnoreConditions { Coupon = "SAVE10", Count = 3, Weight = 7, Secret = "not-stored", Hidden = "not-stored" };

        var result = RoundTrip(original);

        result.Coupon.Should().Be("SAVE10");
        result.Count.Should().Be(3);
        result.Weight.Should().Be(7);
        result.Secret.Should().Be("default", "a bare [JsonIgnore] is Condition = Always");
        result.Hidden.Should().Be("default");
    }

    [Fact(DisplayName = "A null string declared in a nullable-oblivious context comes back null, not empty")]
    public void Oblivious_nulls_round_trip()
    {
        var result = RoundTrip(new ObliviousNotification { Coupon = null!, Codes = ["a", null!] });

        result.Coupon.Should().BeNull();
        result.Codes.Should().Equal("a", null);
    }

    [Fact(DisplayName = "A payload stored before members were added reads back with their initializers")]
    public void Absent_members_keep_their_initializers()
    {
        var orderId = Guid.NewGuid();

        var result = Read<Evolved>($$"""{"orderId":"{{orderId}}"}""");

        result.OrderId.Should().Be(orderId);
        result.Tags.Should().NotBeNull().And.BeEmpty();
        result.Channel.Should().Be("web");
        result.Notify.Should().BeTrue();
        result.Ship.City.Should().Be("default");
    }

    [Fact(DisplayName = "A positional record reads a missing constructor parameter as its default, and a missing init member as its initializer")]
    public void Absent_constructor_parameters_keep_their_defaults()
    {
        var orderId = Guid.NewGuid();

        var result = Read<EvolvedRecord>($$"""{"orderId":"{{orderId}}"}""");

        result.OrderId.Should().Be(orderId);
        result.Channel.Should().Be("web");
        result.Priority.Should().Be(5);
        result.Rate.Should().Be(1.5m);
        result.Tags.Should().NotBeNull().And.BeEmpty();
    }

    [Fact(DisplayName = "A nested object missing a member keeps that member's initializer")]
    public void Nested_absent_members_keep_their_initializers()
    {
        var result = Read<EvolvedParent>("""{"child":{}}""");

        result.Child.Level.Should().Be(3);
        result.Child.Name.Should().Be("n");
    }

    [Fact(DisplayName = "A payload missing a required member is a JsonException; one that has it keeps the other members' initializers")]
    public void Absent_required_member_is_a_json_exception()
    {
        var serializer = Serializer();
        serializer.TryGetNotificationName(typeof(RequiredMember), out var name).Should().BeTrue();

        var read = () => serializer.Deserialize(name!, "{}"u8.ToArray());
        read.Should().Throw<JsonException>().WithMessage("*lacks its required member 'id'*");

        var id = Guid.NewGuid();
        var result = Read<RequiredMember>($$"""{"id":"{{id}}"}""");
        result.Id.Should().Be(id);
        result.Note.Should().Be("x");
    }

    [Fact(DisplayName = "A complete payload builds the object once; a template is built only for a payload that lacks a member")]
    public void Complete_payloads_build_once()
    {
        var serializer = Serializer();
        serializer.TryGetNotificationName(typeof(Counted), out var name).Should().BeTrue();

        Counted.Constructions = 0;
        serializer.Deserialize(name!, """{"a":1,"b":2}"""u8.ToArray());
        Counted.Constructions.Should().Be(1);

        Counted.Constructions = 0;
        serializer.Deserialize(name!, """{"a":1}"""u8.ToArray()).Should().BeOfType<Counted>().Which.B.Should().Be(20);
        Counted.Constructions.Should().Be(2);
    }

    [NotificationName("gen3.outbox.scalars")]
    public sealed class BclScalars : INotification
    {
        public char Grade { get; set; }
        public char? MaybeGrade { get; set; }
        public DateOnly Day { get; set; }
        public DateOnly? MaybeDay { get; set; }
        public TimeOnly At { get; set; }
        public TimeOnly? MaybeAt { get; set; }
        public TimeSpan Took { get; set; }
        public Uri? Link { get; set; }
        public Uri? Missing { get; set; }

        public static BclScalars Sample() => new()
        {
            Grade = 'B',
            MaybeGrade = '"',
            Day = new DateOnly(2024, 2, 29),
            MaybeDay = null,
            At = new TimeOnly(13, 45, 30, 125),
            MaybeAt = new TimeOnly(0, 0),
            Took = new TimeSpan(1, 2, 3, 4),
            Link = new Uri("https://example.com/a?b=c"),
            Missing = null
        };
    }

    [NotificationName("gen3.outbox.keyed")]
    public sealed class Keyed : INotification
    {
        public HashSet<string> Tags { get; set; } = [];
        public ISet<int> Codes { get; set; } = new HashSet<int>();
        public Dictionary<string, int> Counts { get; set; } = [];
        public IDictionary<string, Address> ByName { get; set; } = new Dictionary<string, Address>();
        public IReadOnlyDictionary<string, List<int>>? Buckets { get; set; }
        public Dictionary<string, string?> Notes { get; set; } = [];

        public static Keyed Sample() => new()
        {
            Tags = ["b", "a"],
            Codes = new HashSet<int> { 3, 1, 2 },
            Counts = new Dictionary<string, int> { ["x"] = 1, ["Y z"] = 2 },
            ByName = new Dictionary<string, Address> { ["home"] = new() { City = "Oslo" } },
            Buckets = new Dictionary<string, List<int>> { ["even"] = [2, 4], ["none"] = [] },
            Notes = new Dictionary<string, string?> { ["set"] = "v", ["unset"] = null }
        };
    }

    [NotificationName("gen3.outbox.read-only-set")]
    public sealed class ReadOnlySetNotification : INotification
    {
        public IReadOnlySet<Guid> Ids { get; set; } = new HashSet<Guid>();
    }

    [NotificationName("gen3.outbox.element-nullability")]
    public sealed class ElementNullability : INotification
    {
        public List<string?> Maybe { get; set; } = [];
        public List<string> Never { get; set; } = [];
    }

    public sealed class Address
    {
        public string City { get; set; } = "default";
    }

    [NotificationName("gen3.outbox.marked-ctor")]
    public sealed class MarkedConstructor : INotification
    {
        [JsonConstructor]
        public MarkedConstructor(string name, int count)
        {
            Name = name;
            Count = count;
            ViaMarkedConstructor = true;
        }

        // Maps onto the same members: without [JsonConstructor] the choice would be ambiguous.
        public MarkedConstructor(int count, string name)
        {
            Name = name;
            Count = count;
        }

        public string Name { get; }
        public int Count { get; }

        [JsonIgnore]
        public bool ViaMarkedConstructor { get; }
    }

    [NotificationName("gen3.outbox.ignore")]
    public sealed class IgnoreConditions : INotification
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Coupon { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public int Count { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Weight { get; set; }

        [JsonIgnore]
        public string Secret { get; set; } = "default";

        [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
        public string Hidden { get; set; } = "default";
    }

#nullable disable
    [NotificationName("gen3.outbox.oblivious")]
    public sealed class ObliviousNotification : INotification
    {
        public string Coupon { get; set; }
        public List<string> Codes { get; set; }
    }
#nullable restore

    [NotificationName("gen3.outbox.evolved")]
    public sealed class Evolved : INotification
    {
        public Guid OrderId { get; init; }
        public List<string> Tags { get; init; } = [];
        public string Channel { get; set; } = "web";
        public bool Notify { get; init; } = true;
        public Address Ship { get; init; } = new();
    }

    [NotificationName("gen3.outbox.evolved-record")]
    public sealed record EvolvedRecord(Guid OrderId, string Channel = "web", int Priority = 5, decimal Rate = 1.5m) : INotification
    {
        public List<string> Tags { get; init; } = [];
    }

    [NotificationName("gen3.outbox.evolved-parent")]
    public sealed class EvolvedParent : INotification
    {
        public EvolvedChild Child { get; init; } = new();
    }

    public sealed class EvolvedChild
    {
        public int Level { get; init; } = 3;
        public string Name { get; init; } = "n";
    }

    [NotificationName("gen3.outbox.required")]
    public sealed class RequiredMember : INotification
    {
        public required Guid Id { get; init; }
        public string Note { get; init; } = "x";
    }

    [NotificationName("gen3.outbox.counted")]
    public sealed class Counted : INotification
    {
        public static int Constructions;

        public Counted() => Constructions++;

        public int A { get; init; } = 10;
        public int B { get; init; } = 20;
    }
}
