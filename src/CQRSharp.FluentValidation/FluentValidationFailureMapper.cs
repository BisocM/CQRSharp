using FluentValidationFailure = FluentValidation.Results.ValidationFailure;

namespace CQRSharp.FluentValidation;

/// <summary>
///     Maps FluentValidation failures to CQRSharp's <see cref="ValidationFailure" />.
/// </summary>
public static class FluentValidationFailureMapper
{
    /// <summary>
    ///     The <see cref="ValidationFailure.Code" /> used when a FluentValidation failure carries no error code, which
    ///     happens for failures constructed by hand (for example via <c>context.AddFailure(property, message)</c>).
    ///     Rule-produced failures always carry one (for example <c>NotEmptyValidator</c>, or your <c>WithErrorCode</c>).
    /// </summary>
    public const string UnspecifiedErrorCode = "FluentValidation.Unspecified";

    /// <summary>
    ///     Maps a single FluentValidation failure: <c>ErrorCode</c> to <see cref="ValidationFailure.Code" />,
    ///     <c>ErrorMessage</c> to <see cref="ValidationFailure.Message" /> and <c>PropertyName</c> to
    ///     <see cref="ValidationFailure.MemberName" /> (<see langword="null" /> for a model-level failure with no
    ///     property). The attempted value, custom state, severity and message placeholders have no counterpart on
    ///     <see cref="ValidationFailure" /> and are not carried over.
    /// </summary>
    /// <param name="failure">The FluentValidation failure.</param>
    /// <returns>The equivalent CQRSharp failure.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="failure" /> is <see langword="null" />.</exception>
    public static ValidationFailure Map(FluentValidationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new ValidationFailure(
            string.IsNullOrEmpty(failure.ErrorCode) ? UnspecifiedErrorCode : failure.ErrorCode,
            failure.ErrorMessage ?? string.Empty,
            string.IsNullOrEmpty(failure.PropertyName) ? null : failure.PropertyName);
    }
}
