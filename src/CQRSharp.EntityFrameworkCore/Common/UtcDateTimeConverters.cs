using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     Every timestamp the stores persist is UTC, and every comparison is against <c>TimeProvider.GetUtcNow()</c>. Some
///     providers (SQLite) store a <see cref="DateTime" /> without its kind, so a value read back is
///     <see cref="DateTimeKind.Unspecified" />: these converters re-stamp UTC on the way out, and on the way in treat an
///     unspecified kind as the UTC it is documented to be (only a local time is converted).
/// </summary>
internal static class UtcDateTimeConverters
{
    public static readonly ValueConverter<DateTime, DateTime> Instance = new(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    public static readonly ValueConverter<DateTime?, DateTime?> Nullable = new(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Local ? v.Value.ToUniversalTime() : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}
