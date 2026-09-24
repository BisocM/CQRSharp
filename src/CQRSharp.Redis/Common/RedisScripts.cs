using StackExchange.Redis;

namespace CQRSharp.Redis;

/// <summary>
///     Runs a Lua script through StackExchange.Redis's EVALSHA cache and falls back to a fresh EVAL when the server
///     reports NOSCRIPT (a restart, a failover, a SCRIPT FLUSH), so no store pays that with a failed call.
/// </summary>
/// <remarks>
///     Nothing here translates cluster slot errors: every key a script touches shares the hash tag its store's
///     <c>KeyPrefix</c> must carry, which the options validation enforces at host start.
/// </remarks>
internal static class RedisScripts
{
    public static async Task<RedisResult> EvaluateAsync(IDatabase db, string script, RedisKey[] keys, RedisValue[] args)
    {
        try
        {
            return await db.ScriptEvaluateAsync(script, keys, args).ConfigureAwait(false);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("NOSCRIPT", StringComparison.Ordinal))
        {
            return await db.ScriptEvaluateAsync(script, keys, args).ConfigureAwait(false);
        }
    }
}
