using Microsoft.Extensions.Options;

namespace CQRSharp.Redis;

/// <summary>
///     Rejects a <see cref="RedisIdempotencyOptions" /> the idempotency store cannot run with, at host start. Registered
///     once however often the store is registered, so a failure is reported once.
/// </summary>
internal sealed class RedisIdempotencyOptionsValidator : IValidateOptions<RedisIdempotencyOptions>
{
    public ValidateOptionsResult Validate(string? name, RedisIdempotencyOptions options)
    {
        if (name is not null && name != Options.DefaultName) return ValidateOptionsResult.Skip;

        var failures = Failures(options).ToList();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static IEnumerable<string> Failures(RedisIdempotencyOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.KeyPrefix))
            yield return "RedisIdempotencyOptions.KeyPrefix must not be null or whitespace.";
        else if (!RedisKeyPrefix.HasHashTag(options.KeyPrefix))
            yield return RedisKeyPrefix.MissingHashTag("RedisIdempotencyOptions.KeyPrefix", options.KeyPrefix, new RedisIdempotencyOptions().KeyPrefix);

        if (options.Retention < TimeSpan.FromMilliseconds(1))
            yield return "RedisIdempotencyOptions.Retention must be at least one millisecond (Redis expiries are whole milliseconds).";
        if (options.Database < -1)
            yield return "RedisIdempotencyOptions.Database must be -1 (the connection's default database) or a database index.";
    }
}
