using CQRSharp.Abstractions.Interfaces.Validation;
using CQRSharp.Abstractions.Models.Validation;
using CQRSharp.Sample.Application.Commands.Requests;

namespace CQRSharp.Sample.Application.Validation;

public sealed class ValidatedCommandValidator : IRequestValidator<ValidatedCommand>
{
    public Task<ValidationFailure[]> ValidateAsync(ValidatedCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Value))
            return Task.FromResult(new[]
            {
                new ValidationFailure(
                    "required",
                    "Value is required.",
                    nameof(ValidatedCommand.Value))
            });

        return Task.FromResult(Array.Empty<ValidationFailure>());
    }
}