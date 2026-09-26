using CQRSharp.Pipelines;

namespace CQRSharp.Core.Outbox;

/// <summary>Rejects an <see cref="InMemoryOutboxStoreOptions" /> the in-memory stores cannot run with, at host start.</summary>
internal sealed class InMemoryOutboxStoreOptionsValidator : OptionsValidator<InMemoryOutboxStoreOptions>
{
    protected override IEnumerable<string> Failures(InMemoryOutboxStoreOptions options)
    {
        if (options.VisibilityTimeout <= TimeSpan.Zero || options.VisibilityTimeout > DurationLimits.Longest)
            yield return "InMemoryOutboxStoreOptions.VisibilityTimeout must be greater than zero and must not exceed 10 years.";
        if (options.InboxRetention <= TimeSpan.Zero || options.InboxRetention > DurationLimits.Longest)
            yield return "InMemoryOutboxStoreOptions.InboxRetention must be greater than zero and must not exceed 10 years.";
        if (options.DeadLetterRetention is { } deadLetters && (deadLetters <= TimeSpan.Zero || deadLetters > DurationLimits.Longest))
            yield return "InMemoryOutboxStoreOptions.DeadLetterRetention must be greater than zero and must not exceed 10 years when set.";
    }
}
