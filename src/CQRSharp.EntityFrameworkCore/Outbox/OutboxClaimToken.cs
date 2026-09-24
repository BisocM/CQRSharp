using System.Globalization;

namespace CQRSharp.EntityFrameworkCore;

/// <summary>
///     What the EF Core outbox store's opaque claim token carries: the row version the claim (or its latest renewal)
///     wrote, and the attempt count the row had then. Every write to a row changes its version, so a row that still
///     carries both is exactly as the claim left it: nobody has reclaimed, renewed or finalized it since. That makes every
///     operation under a claim one conditional UPDATE, and lets a failed attempt be counted without reading the row.
/// </summary>
/// <param name="RowVersion">The row version the claim wrote.</param>
/// <param name="AttemptCount">The attempt count of the row when it was claimed.</param>
internal readonly record struct OutboxClaimToken(uint RowVersion, int AttemptCount)
{
    private const char Separator = '.';

    /// <summary>The token of the row as this write leaves it: the next version, which is how a write ends every other claim on the row.</summary>
    /// <remarks>
    ///     Computed here rather than as <c>RowVersion + 1</c> in SQL: the column is wider than the property on providers
    ///     without an unsigned 32-bit type, so the database would store 2^32 where the property wraps to zero.
    /// </remarks>
    public OutboxClaimToken Next() => this with { RowVersion = unchecked(RowVersion + 1) };

    /// <summary>The token as the claim hands it out.</summary>
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{RowVersion}{Separator}{AttemptCount}");

    /// <summary>Reads a token this store issued; anything else (another store's token, a made-up one) is not a claim on any row.</summary>
    public static bool TryParse(string? token, out OutboxClaimToken claimToken)
    {
        claimToken = default;
        if (token is null) return false;

        var separator = token.IndexOf(Separator);
        if (separator < 0 ||
            !uint.TryParse(token.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var rowVersion) ||
            !int.TryParse(token.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var attemptCount))
            return false;

        claimToken = new OutboxClaimToken(rowVersion, attemptCount);
        return true;
    }
}
