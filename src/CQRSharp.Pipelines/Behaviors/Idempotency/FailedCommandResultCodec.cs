using System.Text;

namespace CQRSharp.Pipelines;

/// <summary>
///     The stored form of a failed plain <see cref="CommandResult" />, so the duplicate of a command whose failure was
///     committed gets that same failure back. A successful plain result is stored as nothing at all; only a failure has
///     anything to say. Written field by field, with no reflection.
/// </summary>
internal static class FailedCommandResultCodec
{
    // Leads every record, so a payload of another shape (or a future layout) is recognised as unreadable, not misread.
    private const byte FormatVersion = 1;

    /// <summary>Writes <paramref name="failure" />, which must be a failure, as a replayable record.</summary>
    public static byte[] Serialize(CommandResult failure)
    {
        if (failure.IsSuccess)
            throw new ArgumentException("Only a failed result is stored; a success is stored as no payload.", nameof(failure));

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(FormatVersion);
            writer.Write((int)failure.ErrorKind);
            WriteNullable(writer, failure.ErrorMessage);
            writer.Write(failure.ErrorCode.HasValue);
            if (failure.ErrorCode is { } code) writer.Write(code);

            writer.Write(failure.ValidationFailures.Count);
            foreach (var validationFailure in failure.ValidationFailures)
            {
                writer.Write(validationFailure.Code);
                writer.Write(validationFailure.Message);
                WriteNullable(writer, validationFailure.MemberName);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Reads back a record written by <see cref="Serialize" />; <c>false</c> for anything else.</summary>
    public static bool TryDeserialize(byte[] payload, out CommandResult failure)
    {
        failure = null!;
        try
        {
            using var reader = new BinaryReader(new MemoryStream(payload, writable: false), Encoding.UTF8);
            if (reader.ReadByte() != FormatVersion) return false;

            var kind = (CommandErrorKind)reader.ReadInt32();
            var message = ReadNullable(reader);
            int? code = reader.ReadBoolean() ? reader.ReadInt32() : null;

            // Checked before anything is allocated for it: every entry takes at least three bytes (two string length
            // prefixes and the member name's flag), so a count the rest of the payload cannot hold is not a record of ours.
            var count = reader.ReadInt32();
            if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / 3) return false;
            var validationFailures = new ValidationFailure[count];
            for (var i = 0; i < count; i++)
                validationFailures[i] = new ValidationFailure(reader.ReadString(), reader.ReadString(), ReadNullable(reader));

            if (reader.BaseStream.Position != reader.BaseStream.Length) return false;

            failure = kind switch
            {
                CommandErrorKind.None => null!,
                CommandErrorKind.Validation => CommandResult.Invalid(validationFailures, message, code),
                _ => CommandResult.FromError(kind, message!, code)
            };
            return failure is not null;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException or FormatException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static void WriteNullable(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null) writer.Write(value);
    }

    private static string? ReadNullable(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;
}
