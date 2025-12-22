using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Data.Interfaces.Validation;
using CQRSharp.Abstractions.Data.Models.Validation;
using CQRSharp.Core.Pipelines;

namespace CQRSharp.Pipelines.Types.Validation;

/// <summary>
///     Validates requests using all registered <see cref="IRequestValidator{TRequest}" /> instances.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
public sealed class ValidationBehavior<TRequest, TResult>(
    IEnumerable<IRequestValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior where TRequest : IRequest
{
    public int PipelineExecutionPriority => -50;

    public async Task<TResult> Handle(
        TRequest request,
        Func<CancellationToken, Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<ValidationFailure>? failures = null;
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Length == 0) continue;

            failures ??= new List<ValidationFailure>(result.Length);
            failures.AddRange(result);
        }

        if (failures is { Count: > 0 })
            throw new RequestValidationException(typeof(TRequest), failures);

        return await next(cancellationToken).ConfigureAwait(false);
    }
}
