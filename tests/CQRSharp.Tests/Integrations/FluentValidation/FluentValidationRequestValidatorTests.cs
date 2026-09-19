using CQRSharp.FluentValidation;
using FluentAssertions;
using FluentValidation;
using CqrsFailure = CQRSharp.ValidationFailure;
using FvFailure = FluentValidation.Results.ValidationFailure;

namespace CQRSharp.Tests.Integrations.FluentValidation;

/// <summary>
///     Unit tests for the adapter on its own: failure mapping, the severity filter, the no-validator no-op, combining
///     several validators, and cancellation-token flow.
/// </summary>
public sealed class FluentValidationRequestValidatorTests
{
    [Fact(DisplayName = "Adapter: error code, message and property name are mapped faithfully")]
    public async Task Maps_failures_faithfully()
    {
        var adapter = new FluentValidationRequestValidator<FvOrderQuery>([new NameAndQuantityValidator()]);

        var failures = await adapter.ValidateAsync(new FvOrderQuery { Name = "", Quantity = 0 }, CancellationToken.None);

        failures.Should().Equal(
            new CqrsFailure("NAME_REQUIRED", "A name is required.", nameof(FvOrderQuery.Name)),
            new CqrsFailure("GreaterThanValidator", "'Quantity' must be greater than '0'.", nameof(FvOrderQuery.Quantity)));
    }

    [Fact(DisplayName = "Adapter: a valid request yields no failures")]
    public async Task Valid_request_passes()
    {
        var adapter = new FluentValidationRequestValidator<FvOrderQuery>([new NameAndQuantityValidator()]);

        var failures = await adapter.ValidateAsync(new FvOrderQuery { Name = "widget", Quantity = 1 }, CancellationToken.None);

        failures.Should().BeEmpty();
    }

    [Fact(DisplayName = "Adapter: Warning and Info failures do not fail the request; Error failures still do")]
    public async Task Only_error_severity_is_reported()
    {
        var adapter = new FluentValidationRequestValidator<FvOrderQuery>([new MixedSeverityValidator()]);

        var failures = await adapter.ValidateAsync(new FvOrderQuery { Name = "", Quantity = 0 }, CancellationToken.None);

        failures.Should().ContainSingle().Which.Code.Should().Be("QUANTITY_ERROR");
    }

    [Fact(DisplayName = "Adapter: a request whose only failures are warnings passes")]
    public async Task Warnings_alone_pass()
    {
        var adapter = new FluentValidationRequestValidator<FvOrderQuery>([new MixedSeverityValidator()]);

        var failures = await adapter.ValidateAsync(new FvOrderQuery { Name = "", Quantity = 5 }, CancellationToken.None);

        failures.Should().BeEmpty();
    }

    [Fact(DisplayName = "Adapter: no FluentValidation validator means no failures")]
    public async Task No_validator_is_a_no_op()
    {
        var adapter = new FluentValidationRequestValidator<FvOrderQuery>([]);

        var failures = await adapter.ValidateAsync(new FvOrderQuery(), CancellationToken.None);

        failures.Should().BeEmpty();
    }

    [Fact(DisplayName = "Adapter: several validators all run and their failures combine in registration order")]
    public async Task Multiple_validators_combine()
    {
        var adapter = new FluentValidationRequestValidator<FvOrderQuery>(
            [new NameAndQuantityValidator(), new NameLengthValidator()]);

        var failures = await adapter.ValidateAsync(new FvOrderQuery { Name = "", Quantity = 1 }, CancellationToken.None);

        failures.Select(f => f.Code).Should().Equal("NAME_REQUIRED", "NAME_TOO_SHORT");
    }

    [Fact(DisplayName = "Adapter: the cancellation token reaches asynchronous rules")]
    public async Task Cancellation_token_flows_to_rules()
    {
        using var cts = new CancellationTokenSource();
        var validator = new TokenCapturingValidator();
        var adapter = new FluentValidationRequestValidator<FvOrderQuery>([validator]);

        await adapter.ValidateAsync(new FvOrderQuery { Name = "widget" }, cts.Token);

        validator.Seen.Should().Be(cts.Token);
    }

    [Fact(DisplayName = "Adapter: an already-cancelled token cancels validation")]
    public async Task Cancelled_token_cancels()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var adapter = new FluentValidationRequestValidator<FvOrderQuery>([new TokenCapturingValidator()]);

        var act = () => adapter.ValidateAsync(new FvOrderQuery { Name = "widget" }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact(DisplayName = "Adapter: null arguments are rejected")]
    public async Task Null_arguments_throw()
    {
        var construct = () => new FluentValidationRequestValidator<FvOrderQuery>(null!);
        var validate = () => new FluentValidationRequestValidator<FvOrderQuery>([]).ValidateAsync(null!, CancellationToken.None);

        construct.Should().Throw<ArgumentNullException>();
        await validate.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact(DisplayName = "Mapper: a hand-built failure with no code or property maps to the fallback code and a null member")]
    public void Mapper_handles_missing_code_and_property()
    {
        var mapped = FluentValidationFailureMapper.Map(new FvFailure(string.Empty, "Order is inconsistent."));

        mapped.Should().Be(new CqrsFailure(FluentValidationFailureMapper.UnspecifiedErrorCode, "Order is inconsistent."));
        mapped.MemberName.Should().BeNull();
    }

    // FluentValidation validators carry no CQRSharp interface, so the source generator never registers them; they are
    // private anyway to keep them out of any assembly scan.
    private sealed class NameAndQuantityValidator : AbstractValidator<FvOrderQuery>
    {
        public NameAndQuantityValidator()
        {
            RuleFor(q => q.Name).NotEmpty().WithErrorCode("NAME_REQUIRED").WithMessage("A name is required.");
            RuleFor(q => q.Quantity).GreaterThan(0);
        }
    }

    private sealed class NameLengthValidator : AbstractValidator<FvOrderQuery>
    {
        public NameLengthValidator()
        {
            RuleFor(q => q.Name).MinimumLength(3).WithErrorCode("NAME_TOO_SHORT");
        }
    }

    private sealed class MixedSeverityValidator : AbstractValidator<FvOrderQuery>
    {
        public MixedSeverityValidator()
        {
            RuleFor(q => q.Name).NotEmpty().WithErrorCode("NAME_WARNING").WithSeverity(Severity.Warning);
            RuleFor(q => q.Name).MinimumLength(3).WithErrorCode("NAME_INFO").WithSeverity(Severity.Info);
            RuleFor(q => q.Quantity).GreaterThan(0).WithErrorCode("QUANTITY_ERROR");
        }
    }

    private sealed class TokenCapturingValidator : AbstractValidator<FvOrderQuery>
    {
        public CancellationToken Seen { get; private set; }

        public TokenCapturingValidator()
        {
            RuleFor(q => q.Name).MustAsync((_, token) =>
            {
                Seen = token;
                return Task.FromResult(true);
            });
        }
    }
}
