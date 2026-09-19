using Xunit;

namespace CQRSharp.Testing;

/// <summary>
///     The few assertions the contract suites need, on top of plain <see cref="Assert" />. They exist so a failing
///     store gets a message that names the violated contract rule (xUnit's own <c>Assert.Equal</c>/<c>Assert.Empty</c>
///     take no user message), without the package depending on an assertion library its consumers would inherit.
/// </summary>
internal static class ContractAssert
{
    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            Assert.Fail($"{what}: expected {Format(expected)}, but found {Format(actual)}.");
    }

    public static void NotEqual<T>(T unexpected, T actual, string what)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
            Assert.Fail($"{what}: did not expect {Format(actual)}.");
    }

    public static void CloseTo(DateTime expected, DateTime actual, TimeSpan tolerance, string what)
    {
        if ((expected - actual).Duration() > tolerance)
            Assert.Fail($"{what}: expected {expected:O} (+/- {tolerance}), but found {actual:O}.");
    }

    public static void Empty<T>(IEnumerable<T> items, string because)
    {
        var list = items.ToList();
        if (list.Count != 0)
            Assert.Fail($"Expected no items because {because}, but found {list.Count}: {FormatAll(list)}.");
    }

    public static T Single<T>(IEnumerable<T> items, string because)
    {
        var list = items.ToList();
        if (list.Count != 1)
            Assert.Fail($"Expected exactly one item because {because}, but found {list.Count}: {FormatAll(list)}.");

        return list[0];
    }

    public static void Count<T>(int expected, IReadOnlyCollection<T> items, string because)
    {
        if (items.Count != expected)
            Assert.Fail($"Expected {expected} item(s) because {because}, but found {items.Count}.");
    }

    public static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string what)
    {
        if (!expected.SequenceEqual(actual))
            Assert.Fail($"{what}: expected [{FormatAll(expected)}], but found [{FormatAll(actual)}].");
    }

    private static string FormatAll<T>(IEnumerable<T> items) => string.Join(", ", items.Select(Format));

    private static string Format<T>(T value) => value switch
    {
        null => "<null>",
        string text => $"\"{text}\"",
        _ => value.ToString() ?? "<null>"
    };
}
