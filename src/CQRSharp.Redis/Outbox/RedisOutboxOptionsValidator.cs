using Microsoft.Extensions.Options;

namespace CQRSharp.Redis;

/// <summary>
///     Rejects a <see cref="RedisOutboxOptions" /> the outbox and inbox stores cannot run with, at host start. Registered
///     once however often the store is registered, so a failure is reported once.
/// </summary>
internal sealed class RedisOutboxOptionsValidator : IValidateOptions<RedisOutboxOptions>
{
    public ValidateOptionsResult Validate(string? name, RedisOutboxOptions options)
    {
        if (name is not null && name != Options.DefaultName) return ValidateOptionsResult.Skip;

        var failures = Failures(options).ToList();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static IEnumerable<string> Failures(RedisOutboxOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.KeyPrefix))
            yield return "RedisOutboxOptions.KeyPrefix must not be null or whitespace.";
        else if (!RedisKeyPrefix.HasHashTag(options.KeyPrefix))
            yield return RedisKeyPrefix.MissingHashTag("RedisOutboxOptions.KeyPrefix", options.KeyPrefix, new RedisOutboxOptions().KeyPrefix);

        if (options.VisibilityTimeout < TimeSpan.FromMilliseconds(1))
            yield return "RedisOutboxOptions.VisibilityTimeout must be at least one millisecond (the store keeps times in whole milliseconds).";
        if (options.DeadLetterRetention is { } deadLetterRetention && deadLetterRetention < TimeSpan.FromMilliseconds(1))
            yield return "RedisOutboxOptions.DeadLetterRetention must be at least one millisecond when set.";
        if (options.InboxRetention < TimeSpan.FromMilliseconds(1))
            yield return "RedisOutboxOptions.InboxRetention must be at least one millisecond (Redis expiries are whole milliseconds).";
        if (options.Database < -1)
            yield return "RedisOutboxOptions.Database must be -1 (the connection's default database) or a database index.";
    }
}
