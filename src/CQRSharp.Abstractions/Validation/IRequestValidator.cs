
namespace CQRSharp;

/// <summary>
///     Validates requests of type <typeparamref name="TRequest" /> before their handler runs. The validation behavior
///     runs every validator for the request and, when any reports a failure, rejects the request with a
///     <see cref="RequestValidationException" /> carrying all of them.
/// </summary>
/// <remarks>
///     The source generator registers every public or internal, non-generic implementation it finds; register one by
///     hand only when the generator cannot see it (a private nested type, an open generic, an assembly without the
///     generator). When the application registers an implementation of the same type itself, its own registration runs
///     instead of the discovered one.
/// </remarks>
/// <typeparam name="TRequest">The request type to validate.</typeparam>
public interface IRequestValidator<in TRequest> where TRequest : IRequest
{
    /// <summary>
    ///     Validates the request instance and returns zero or more validation failures.
    ///     Return <see cref="Array.Empty{T}" /> when the request is valid.
    /// </summary>
    /// <param name="request">The request to validate.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An array of validation failures.</returns>
    Task<ValidationFailure[]> ValidateAsync(TRequest request, CancellationToken cancellationToken);
}