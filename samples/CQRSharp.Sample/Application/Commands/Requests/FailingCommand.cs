using CQRSharp.Sample.Application.Contexts;

namespace CQRSharp.Sample.Application.Commands.Requests;

/// <summary>
///     Fails its first <see cref="FailuresBeforeSuccess" /> attempts, then succeeds. It opts into the resilience
///     behavior's retries with <see cref="IRetryableRequest" />: only a request that is safe to run again may.
/// </summary>
public sealed class FailingCommand : CommandBase<SampleRequestContext>, IRetryableRequest
{
    public Guid Id { get; } = Guid.NewGuid();
    public required int FailuresBeforeSuccess { get; init; }
}
