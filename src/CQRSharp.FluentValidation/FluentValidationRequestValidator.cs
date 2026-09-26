using FluentValidation;

namespace CQRSharp.FluentValidation;

/// <summary>
///     Adapts every registered FluentValidation <see cref="IValidator{T}" /> for <typeparamref name="TRequest" /> to
///     CQRSharp's <see cref="IRequestValidator{TRequest}" />, so existing <c>AbstractValidator&lt;T&gt;</c> classes run
///     inside the CQRSharp validation behavior. It is registered as an open generic by
///     <c>AddCqrsFluentValidation()</c> / <c>UseFluentValidation()</c> and is resolved alongside any native
///     <see cref="IRequestValidator{TRequest}" />; the validation behavior combines the failures of all of them.
/// </summary>
/// <remarks>
///     <para>
///         Only failures with <see cref="Severity.Error" /> are reported. FluentValidation marks a result invalid for
///         <see cref="Severity.Warning" /> and <see cref="Severity.Info" /> failures too, but CQRSharp's
///         <see cref="ValidationFailure" /> carries no severity and any reported failure rejects the request, so
///         passing them through would turn advisory rules into hard failures.
///     </para>
///     <para>
///         A request with no registered FluentValidation validator yields no failures. FluentValidation compiles
///         expression trees at runtime, so this adapter is not Native-AOT or full-trim compatible.
///     </para>
/// </remarks>
/// <typeparam name="TRequest">The request type to validate.</typeparam>
public sealed class FluentValidationRequestValidator<TRequest> : IRequestValidator<TRequest>
    where TRequest : IRequest
{
    // Shared by every no-failure outcome so the common "nothing to validate" case allocates nothing.
    private static readonly Task<ValidationFailure[]> NoFailures = Task.FromResult(Array.Empty<ValidationFailure>());

    private readonly IValidator<TRequest>[] _validators;

    /// <summary>
    ///     Creates the adapter over every FluentValidation validator registered for <typeparamref name="TRequest" />.
    /// </summary>
    /// <param name="validators">The registered FluentValidation validators; may be empty.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="validators" /> is <see langword="null" />.</exception>
    public FluentValidationRequestValidator(IEnumerable<IValidator<TRequest>> validators)
    {
        ArgumentNullException.ThrowIfNull(validators);
        _validators = validators as IValidator<TRequest>[] ?? validators.ToArray();
    }

    /// <summary>
    ///     Runs every FluentValidation validator asynchronously and returns their <see cref="Severity.Error" />
    ///     failures mapped to CQRSharp failures, in validator registration order.
    /// </summary>
    /// <param name="request">The request to validate.</param>
    /// <param name="cancellationToken">A cancellation token, passed through to each validator.</param>
    /// <returns>The mapped failures, or an empty array when the request is valid or has no FluentValidation validator.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request" /> is <see langword="null" />.</exception>
    public Task<ValidationFailure[]> ValidateAsync(TRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The adapter is registered for every request type, so the common case is "no FluentValidation validator";
        // skip the async state machine entirely for it.
        return _validators.Length == 0 ? NoFailures : ValidateCoreAsync(request, cancellationToken);
    }

    private async Task<ValidationFailure[]> ValidateCoreAsync(TRequest request, CancellationToken cancellationToken)
    {
        List<ValidationFailure>? failures = null;
        foreach (var validator in _validators)
        {
            // A fresh context per validator: a context accumulates failures and root data, and sharing one would
            // leak the previous validator's failures into the next result.
            var context = new ValidationContext<TRequest>(request);
            var result = await validator.ValidateAsync(context, cancellationToken).ConfigureAwait(false);

            foreach (var failure in result.Errors)
            {
                if (failure is null || failure.Severity != Severity.Error) continue;
                (failures ??= []).Add(FluentValidationFailureMapper.Map(failure));
            }
        }

        return failures is null ? [] : failures.ToArray();
    }
}
