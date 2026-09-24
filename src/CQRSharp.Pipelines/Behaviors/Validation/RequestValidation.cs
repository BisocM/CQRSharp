using CQRSharp.Core.Modules;

namespace CQRSharp.Pipelines;

/// <summary>The validator loop the command/query and streaming validation behaviors share.</summary>
internal static class RequestValidation
{
    /// <summary>
    ///     Runs every validator and throws <see cref="RequestValidationException" /> carrying all their failures at once;
    ///     returns normally when the request is valid.
    /// </summary>
    public static async Task ValidateAsync<TRequest>(
        TRequest request,
        IEnumerable<IRequestValidator<TRequest>> validators,
        IEnumerable<IRequestValidator<TRequest>>? discovered,
        CancellationToken cancellationToken)
        where TRequest : IRequest
    {
        List<ValidationFailure>? failures = null;
        foreach (var validator in discovered is null ? validators : DiscoveredServices.Merge(discovered, validators))
        {
            var result = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Length == 0) continue;

            failures ??= new List<ValidationFailure>(result.Length);
            failures.AddRange(result);
        }

        if (failures is { Count: > 0 })
            throw new RequestValidationException(typeof(TRequest), failures);
    }
}
