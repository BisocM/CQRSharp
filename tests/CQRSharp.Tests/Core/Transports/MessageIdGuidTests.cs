using CQRSharp.Core.Transports;
using FluentAssertions;

namespace CQRSharp.Tests.Core;

/// <summary>
///     <see cref="MessageIdGuid" />: the inbox keys a received notification by a <see cref="Guid" />, so a sender's id that is
///     one is used as it is, and any other is mapped to a name-based UUID the same on every instance.
/// </summary>
public sealed class MessageIdGuidTests
{
    [Theory(DisplayName = "A message id that is a Guid, in any of its formats, is that Guid")]
    [InlineData("8f3c2a9e5b0d4c7e9a1f6b2d3c4e5f60")]
    [InlineData("8f3c2a9e-5b0d-4c7e-9a1f-6b2d3c4e5f60")]
    [InlineData("{8f3c2a9e-5b0d-4c7e-9a1f-6b2d3c4e5f60}")]
    public void A_guid_is_used_as_it_is(string messageId)
        => MessageIdGuid.From(messageId).Should().Be(Guid.Parse("8f3c2a9e-5b0d-4c7e-9a1f-6b2d3c4e5f60"));

    [Theory(DisplayName = "Any other message id maps to its RFC 9562 version 5 UUID in CQRSharp's namespace")]
    [InlineData("order-42", "296d8e5b-3772-56da-ab14-1bfbaf39ee59")]
    [InlineData("ünïcode-id", "c99a9fff-892d-5c5c-aec8-c5e5b4b7083c")]
    public void Another_id_maps_to_a_name_based_uuid(string messageId, string expected)
    {
        // The expected values come from Python's uuid.uuid5 over the same namespace, an independent implementation.
        var id = MessageIdGuid.From(messageId);

        id.Should().Be(Guid.Parse(expected));
        MessageIdGuid.From(messageId).Should().Be(id, "the mapping is deterministic");
        MessageIdGuid.From(messageId + "!").Should().NotBe(id);
    }
}
