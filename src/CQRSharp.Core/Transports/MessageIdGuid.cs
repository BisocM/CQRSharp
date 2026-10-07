using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CQRSharp.Core.Transports;

/// <summary>
///     Turns the message id a sender gave a received notification into the <see cref="Guid" /> the inbox records it under:
///     the id itself when it is one, and otherwise a name-based UUID (RFC 9562 version 5) of it, so a foreign sender's ids
///     deduplicate the same on every instance and every redelivery.
/// </summary>
internal static class MessageIdGuid
{
    // A fixed namespace of CQRSharp's own, so these ids never coincide with version-5 ids another system derives from
    // the same strings.
    private static readonly Guid Namespace = new("2d6b4c1e-8f7a-5e3b-9c40-7a1f0e6d2b95");

    /// <summary>The inbox id of <paramref name="messageId" />.</summary>
    public static Guid From(string messageId)
    {
        ArgumentNullException.ThrowIfNull(messageId);
        return Guid.TryParse(messageId, out var id) ? id : NameBased(messageId);
    }

    private static Guid NameBased(string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[16 + nameBytes.Length];
        WriteBigEndian(Namespace, input);
        nameBytes.CopyTo(input, 16);

        // Not a security use: the UUID version 5 algorithm is defined over SHA-1.
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);

        Span<byte> uuid = stackalloc byte[16];
        hash[..16].CopyTo(uuid);
        uuid[6] = (byte)((uuid[6] & 0x0F) | 0x50);
        uuid[8] = (byte)((uuid[8] & 0x3F) | 0x80);
        return ReadBigEndian(uuid);
    }

    // A Guid's first three fields are stored little-endian; the UUID algorithms work on the network (big-endian) order.
    private static void WriteBigEndian(Guid guid, Span<byte> destination)
    {
        guid.TryWriteBytes(destination);
        destination[..4].Reverse();
        destination.Slice(4, 2).Reverse();
        destination.Slice(6, 2).Reverse();
    }

    private static Guid ReadBigEndian(ReadOnlySpan<byte> uuid)
        => new(
            BinaryPrimitives.ReadInt32BigEndian(uuid),
            BinaryPrimitives.ReadInt16BigEndian(uuid[4..]),
            BinaryPrimitives.ReadInt16BigEndian(uuid[6..]),
            uuid[8], uuid[9], uuid[10], uuid[11], uuid[12], uuid[13], uuid[14], uuid[15]);
}
