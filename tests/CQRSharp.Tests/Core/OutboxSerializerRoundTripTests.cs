using System.Text.Json;
using CQRSharp.Abstractions.Attributes.Notifications;
using CQRSharp.Abstractions.Interfaces.Notifications;
using FluentAssertions;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Round-trip tests for the source-generated <c>INotificationSerializer</c>
///     (<c>CQRSharp.Core.Serialization.Generated.GeneratedOutboxNotificationSerializer</c>).
///     The CQRSharp source generator runs as an analyzer on this test project, so the serializer is emitted into the
///     test assembly itself for every <c>INotification</c> decorated with <see cref="NotificationNameAttribute" />
///     (including <see cref="RoundTripNotification" /> declared below). The type is internal-to-the-test-assembly and
///     generated, so it is resolved by reflection rather than a hard compile-time reference.
/// </summary>
public sealed class OutboxSerializerRoundTripTests
{
    private const string GeneratedSerializerTypeName =
        "CQRSharp.Core.Serialization.Generated.GeneratedOutboxNotificationSerializer";

    private const string StableName = "outbox.roundtrip.notification";

    private static INotificationSerializer GetGeneratedSerializer()
    {
        var type = typeof(RoundTripNotification).Assembly.GetType(GeneratedSerializerTypeName, false);
        type.Should().NotBeNull(
            $"the CQRSharp source generator should emit '{GeneratedSerializerTypeName}' into the test assembly " +
            "because at least one notification carries [NotificationName]");

        var instance = Activator.CreateInstance(type!, true);
        return instance.Should().BeAssignableTo<INotificationSerializer>().Subject;
    }

    [Fact(DisplayName = "Generated serializer: Serialize -> Deserialize round-trips all properties")]
    public void Serialize_Then_Deserialize_RoundTripsProperties()
    {
        // Arrange
        var serializer = GetGeneratedSerializer();
        var original = new RoundTripNotification
        {
            Name = "Ada Lovelace",
            UserId = Guid.NewGuid(),
            Count = 42,
            Enabled = true
        };

        var name = serializer.GetNotificationName(typeof(RoundTripNotification));
        name.Should().Be(StableName, "GetNotificationName should return the [NotificationName] value");

        // Act
        var payload = serializer.Serialize(original);
        payload.Should().NotBeNullOrEmpty();

        var deserialized = serializer.Deserialize(name, payload);

        // Assert
        deserialized.Should().BeOfType<RoundTripNotification>();
        var roundTripped = (RoundTripNotification)deserialized!;
        roundTripped.Name.Should().Be(original.Name);
        roundTripped.UserId.Should().Be(original.UserId);
        roundTripped.Count.Should().Be(original.Count);
        roundTripped.Enabled.Should().Be(original.Enabled);
    }

    [Fact(DisplayName = "Generated serializer: an unknown notification name deserializes to null")]
    public void Deserialize_UnknownName_ReturnsNull()
    {
        // Arrange
        var serializer = GetGeneratedSerializer();
        var validPayload = serializer.Serialize(new RoundTripNotification { Name = "x" });

        // Act - the name is unknown even though the payload itself is valid JSON for the known type.
        var result = serializer.Deserialize("totally.unknown.name", validPayload);

        // Assert - unknown name => null (so callers can distinguish 'unknown type' from 'corrupt payload').
        result.Should().BeNull();
    }

    [Fact(DisplayName = "Generated serializer: a corrupt payload for a known name throws JsonException")]
    public void Deserialize_CorruptPayload_ForKnownName_Throws()
    {
        // Arrange
        var serializer = GetGeneratedSerializer();
        var corrupt = "this is not json {"u8.ToArray();

        // Act
        var act = () => serializer.Deserialize(StableName, corrupt);

        // Assert - a corrupt payload for a KNOWN name throws (it must not silently collapse to null).
        act.Should().Throw<JsonException>();
    }

    /// <summary>
    ///     A notification with a stable name and a parameterless constructor plus settable properties, mirroring the
    ///     shape the AOT serializer supports (string, Guid, int, bool). Public so the generator (which only emits for
    ///     accessible types) picks it up.
    /// </summary>
    [NotificationName(StableName)]
    public sealed class RoundTripNotification : INotification
    {
        public string Name { get; set; } = string.Empty;
        public Guid UserId { get; set; }
        public int Count { get; set; }
        public bool Enabled { get; set; }
    }
}