namespace CQRSharp.Core.Notifications;

/// <summary>
///     Ranks the notification types a handler is declared for by how near each is to a runtime type assignable to them:
///     the type itself, then its base classes from the most derived up, then its interfaces. Ties (every interface, in
///     particular) are broken by type name. A handler declared for several of a notification's types runs once, for the
///     nearest, in-process and through the outbox alike; this is the only place that decides which. No reflection over
///     members: the base-class walk is all it needs.
/// </summary>
internal static class NotificationSpecificity
{
    private const int InterfaceBand = int.MaxValue / 2;

    /// <summary>
    ///     The rank of <paramref name="handledType" /> for <paramref name="runtimeType" />; lower is nearer.
    ///     <paramref name="handledType" /> must be assignable from <paramref name="runtimeType" />: the runtime type itself,
    ///     one of its base classes or one of its interfaces.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="handledType" /> is none of them.</exception>
    public static int Rank(Type handledType, Type runtimeType)
    {
        if (handledType == runtimeType) return 0;

        if (handledType.IsInterface) return InterfaceBand;

        var depth = 0;
        for (var type = runtimeType.BaseType; type is not null; type = type.BaseType)
        {
            depth++;
            if (type == handledType) return depth;
        }

        throw new ArgumentException($"'{handledType}' is not assignable from '{runtimeType}'.", nameof(handledType));
    }

    private static string NameOf(Type type) => type.FullName ?? type.Name;

    /// <summary>Orders two handled types of one runtime type: nearer first, then by name.</summary>
    public static int Compare(Type first, Type second, Type runtimeType)
    {
        var byRank = Rank(first, runtimeType).CompareTo(Rank(second, runtimeType));
        return byRank != 0 ? byRank : string.CompareOrdinal(NameOf(first), NameOf(second));
    }
}
