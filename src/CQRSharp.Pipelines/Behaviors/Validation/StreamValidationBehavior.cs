using CQRSharp.Core.Pipelines;

namespace CQRSharp.Pipelines.Behaviors.Validation;

/// <summary>
///     Validates streaming requests using all registered <see cref="IRequestValidator{TRequest}" /> instances.
/// </summary>
public sealed class StreamValidationBehavior<TRequest, TItem>(
    IEnumerable<IRequestValidator<TRequest>> validators)
    : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior
    where TRequest : IRequest
{
    /// <inheritdoc />
    public int PipelineExecutionPriority => -50;

    /// <inheritdoc />
    public IAsyncEnumerable<TItem> Handle(
        TRequest request,
        StreamHandlerDelegate<TItem> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        return ExecuteAsync();

        async IAsyncEnumerable<TItem> ExecuteAsync()
        {
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

            await foreach (var item in next(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
    }
}