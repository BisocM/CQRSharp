using CQRSharp.Pipelines;

namespace CQRSharp.Core.Idempotency;

/// <summary>Rejects an <see cref="InMemoryIdempotencyStoreOptions" /> the in-memory store cannot run with, at host start.</summary>
internal sealed class InMemoryIdempotencyStoreOptionsValidator : OptionsValidator<InMemoryIdempotencyStoreOptions>
{
    protected override IEnumerable<string> Failures(InMemoryIdempotencyStoreOptions options)
    {
        if (options.Retention <= TimeSpan.Zero || options.Retention > DurationLimits.Longest)
            yield return "InMemoryIdempotencyStoreOptions.Retention must be greater than zero and must not exceed 10 years.";
    }
}
