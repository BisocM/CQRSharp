namespace CQRSharp.Tests.Pipelines;

/// <summary>A validator that reports a fixed set of failures and records whether it ran.</summary>
/// <remarks>
///     Generic so that the source generator, which registers every closed validator this assembly declares, leaves it
///     out: registered, it would validate its request in every test that sends one.
/// </remarks>
internal sealed class FakeValidator<TRequest>(params ValidationFailure[] failures) : IRequestValidator<TRequest>
    where TRequest : IRequest
{
    public bool WasCalled { get; private set; }

    public Task<ValidationFailure[]> ValidateAsync(TRequest request, CancellationToken cancellationToken)
    {
        WasCalled = true;
        return Task.FromResult(failures);
    }
}
