using CQRSharp.Core.Pipelines;
using CQRSharp.Core.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace CQRSharp.Pipelines;

/// <summary>
///     Runs every <see cref="IRequestValidator{TRequest}" /> for the request before the handler and rejects the request
///     with a <see cref="RequestValidationException" /> carrying all their failures when any reports one.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
/// <param name="validators">The validators the application registered.</param>
/// <param name="discovered">
///     The validators the source generator discovered; one whose type the application also registered runs once, as the
///     application's.
/// </param>
public sealed class ValidationBehavior<TRequest, TResult>(
    IEnumerable<IRequestValidator<TRequest>> validators,
    [FromKeyedServices(DiscoveredServices.Key)] IEnumerable<IRequestValidator<TRequest>>? discovered = null)
    : IPipelineBehavior<TRequest, TResult>, IPrioritizedPipelineBehavior, ICqrsValidationBehaviorMarker where TRequest : IRequest
{
    /// <inheritdoc />
    public async Task<TResult> Handle(
        TRequest request,
        RequestHandlerDelegate<TResult> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await RequestValidation.ValidateAsync(request, validators, discovered, cancellationToken).ConfigureAwait(false);
        return await next(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public int PipelineExecutionPriority => CqrsPipelinePriorities.Validation;
}
