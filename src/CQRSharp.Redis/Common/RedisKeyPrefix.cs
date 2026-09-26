namespace CQRSharp.Redis;

/// <summary>
///     The rule every store's <c>KeyPrefix</c> must meet. Each store script works on several keys at once, and Redis
///     Cluster (like the cluster-aware proxies) runs a script only when every key it touches lives in one hash slot. A key
///     with a hash tag is slotted by the tag alone, and a tag in the prefix is the whole key's tag, whatever follows it, so
///     it puts every key of the store in one slot. The rule is enforced everywhere, a single server included: without it
///     a store fails only after moving to a cluster, partway through a script's writes, with an error that names neither
///     the store nor its prefix.
/// </summary>
internal static class RedisKeyPrefix
{
    /// <summary>
    ///     Whether <paramref name="prefix" /> carries a hash tag as Redis reads one: the text between the first <c>{</c>
    ///     and the first <c>}</c> after it, which must not be empty.
    /// </summary>
    public static bool HasHashTag(string prefix)
    {
        var open = prefix.IndexOf('{');
        if (open < 0) return false;

        var close = prefix.IndexOf('}', open + 1);
        return close > open + 1;
    }

    /// <summary>The validation failure for a prefix without a hash tag, naming the option and the prefix.</summary>
    public static string MissingHashTag(string option, string prefix, string example)
        => $"{option} '{prefix}' has no hash tag. The store's scripts touch several keys at once, which Redis Cluster allows only " +
           "when all of them hash to one slot, and a non-empty {...} part in the prefix is what puts them there. Use a prefix " +
           $"with one, such as the default '{example}'.";
}
