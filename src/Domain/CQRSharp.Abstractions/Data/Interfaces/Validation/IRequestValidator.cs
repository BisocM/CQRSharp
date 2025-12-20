using CQRSharp.Abstractions.Data.Interfaces.Markers.Request;
using CQRSharp.Abstractions.Data.Models.Validation;

namespace CQRSharp.Abstractions.Data.Interfaces.Validation;

/// <summary>
///     Defines a validator for a specific request type. Implementations should be registered in DI.
/// </summary>
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

