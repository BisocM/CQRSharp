using CQRSharp.Core.Pipelines;
using CQRSharp.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Pipelines;

/// <summary>
///     The streaming counterpart of <see cref="ValidationBehavior{TRequest,TResult}" />: runs every
///     <see cref="IRequestValidator{TRequest}" /> for the request when the stream is enumerated, before the handler
///     produces an item, and rejects it with a <see cref="RequestValidationException" /> when any reports a failure.
/// </summary>
/// <typeparam name="TRequest">The streaming request type.</typeparam>
/// <typeparam name="TItem">The streamed item type.</typeparam>
/// <param name="validators">The validators the application registered.</param>
/// <param name="discovered">
///     The validators the source generator discovered; one whose type the application also registered runs once, as the
///     application's.
/// </param>
public sealed class StreamValidationBehavior<TRequest, TItem>(
    IEnumerable<IRequestValidator<TRequest>> validators,
    [FromKeyedServices(DiscoveredServices.Key)] IEnumerable<IRequestValidator<TRequest>>? discovered = null)
    : IStreamPipelineBehavior<TRequest, TItem>, IPrioritizedPipelineBehavior, ICqrsValidationBehaviorMarker
    where TRequest : IRequest
{
    /// <inheritdoc />
    public int PipelineExecutionPriority => CqrsPipelinePriorities.Validation;

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
            await RequestValidation.ValidateAsync(request, validators, discovered, cancellationToken).ConfigureAwait(false);

            await foreach (var item in next(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
    }
}
