namespace CQRSharp.Sample.Infrastructure.SelfTest;

/// <summary>A scoped service whose id names the DI scope it was resolved from, so a handler can report where it ran.</summary>
public sealed class SampleScopedMarker
{
    public Guid Id { get; } = Guid.NewGuid();
}
