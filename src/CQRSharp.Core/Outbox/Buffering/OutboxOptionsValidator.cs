namespace CQRSharp.Core.Outbox;

/// <summary>Rejects an <see cref="OutboxOptions" /> whose mode is not defined (a number bound from configuration), at host start.</summary>
internal sealed class OutboxOptionsValidator : OptionsValidator<OutboxOptions>
{
    protected override IEnumerable<string> Failures(OutboxOptions options)
    {
        if (!Enum.IsDefined(options.Mode))
            yield return "OutboxOptions.Mode must be a defined OutboxMode.";
    }
}
