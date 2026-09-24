using System.ComponentModel;
using System.Globalization;

namespace CQRSharp.Core.Outbox;

/// <summary>
///     Turns the value of a <c>[NotificationName(PartitionBy = ...)]</c> property into the string the outbox partitions
///     on. Called by source-generated partition key selectors; public for that reason.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class OutboxPartitionKey
{
    /// <summary>
    ///     Formats a property value as a partition key: <see langword="null" /> stays <see langword="null" /> (an
    ///     unordered delivery), a string is used as is, and anything else is rendered culture-invariantly so a numeric or
    ///     date key means the same thing on every machine.
    /// </summary>
    /// <typeparam name="TValue">The property's type.</typeparam>
    /// <param name="value">The property's value.</param>
    /// <returns>The key, or <see langword="null" />.</returns>
    public static string? From<TValue>(TValue value)
        => value switch
        {
            null => null,
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
}
