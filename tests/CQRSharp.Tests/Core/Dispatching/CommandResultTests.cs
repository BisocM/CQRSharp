using FluentAssertions;

namespace CQRSharp.Tests.Core;

/// <summary>
///     <see cref="CommandResult" /> and <see cref="CommandResult{T}" />: every factory, the normalization of what the
///     public constructor is given, the error kinds, and value equality.
/// </summary>
public sealed class CommandResultTests
{
    // ---------------------------------------------------------------------
    // CommandResult
    // ---------------------------------------------------------------------

    [Fact]
    public void FromSuccess_ProducesSuccessResultWithNoError()
    {
        var result = CommandResult.FromSuccess();

        result.IsSuccess.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.ErrorCode.Should().BeNull();
    }

    [Fact(DisplayName = "The public constructor normalizes a success that smuggles error state")]
    public void Constructor_normalizes_a_success()
    {
        var failure = new ValidationFailure("A", "a", "M");

        var result = new CommandResult<int>(true, 1, CommandErrorKind.None, "boom", 7, new[] { failure });

        result.Should().Be(CommandResult<int>.FromSuccess(1));
        result.ErrorMessage.Should().BeNull();
        result.ErrorCode.Should().BeNull();
        result.ValidationFailures.Should().BeEmpty();
    }

    [Fact(DisplayName = "The public constructor drops the value of a failure and the failures of a non-validation kind")]
    public void Constructor_normalizes_a_failure()
    {
        var failure = new ValidationFailure("A", "a", "M");

        var notFound = new CommandResult<Guid>(false, Guid.NewGuid(), CommandErrorKind.NotFound, "missing", null, null);
        var conflict = new CommandResult<int>(false, 0, CommandErrorKind.Conflict, "taken", null, new[] { failure });

        notFound.Value.Should().Be(Guid.Empty);
        notFound.ErrorKind.Should().Be(CommandErrorKind.NotFound);
        conflict.ValidationFailures.Should().BeEmpty("only a validation failure carries validation failures");
    }

    [Fact(DisplayName = "Invalid(null) throws like the list overload")]
    public void Invalid_rejects_a_null_array()
    {
        var act = () => CommandResult.Invalid((ValidationFailure[])null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FromSuccess_ToString_DescribesSuccess()
    {
        CommandResult.FromSuccess().ToString().Should().Be("Command succeeded.");
    }

    [Fact]
    public void FromError_WithMessageOnly_SetsFailureAndLeavesCodeNull()
    {
        var result = CommandResult.FromError("boom");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("boom");
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void FromError_WithMessageAndCode_SetsAllFailureMembers()
    {
        var result = CommandResult.FromError("not found", 404);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Be("not found");
        result.ErrorCode.Should().Be(404);
    }

    [Fact]
    public void FromError_ToString_DescribesFailureWithKindMessageAndCode()
    {
        var result = CommandResult.FromError("not found", 404);

        result.ToString().Should().Be("Command failed [Failure]: not found (Code: 404)");
    }

    [Fact]
    public void FromError_ToString_WithoutCode_OmitsTheCodeSegment()
    {
        var result = CommandResult.NotFound("boom");

        result.ToString().Should().Be("Command failed [NotFound]: boom");
    }

    // ---------------------------------------------------------------------
    // Error kinds
    // ---------------------------------------------------------------------

    [Fact]
    public void FromSuccess_HasNoErrorKindAndNoValidationFailures()
    {
        var result = CommandResult.FromSuccess();

        result.ErrorKind.Should().Be(CommandErrorKind.None);
        result.ValidationFailures.Should().BeEmpty();
    }

    [Fact]
    public void FromError_WithoutKind_IsAPlainFailure()
    {
        CommandResult.FromError("boom").ErrorKind.Should().Be(CommandErrorKind.Failure);
        CommandResult<int>.FromError("boom").ErrorKind.Should().Be(CommandErrorKind.Failure);
    }

    [Theory]
    [InlineData(CommandErrorKind.Failure)]
    [InlineData(CommandErrorKind.Validation)]
    [InlineData(CommandErrorKind.NotFound)]
    [InlineData(CommandErrorKind.Conflict)]
    [InlineData(CommandErrorKind.Unauthorized)]
    [InlineData(CommandErrorKind.Forbidden)]
    [InlineData(CommandErrorKind.Unavailable)]
    public void FromError_WithKind_CarriesTheKind(CommandErrorKind kind)
    {
        var plain = CommandResult.FromError(kind, "why", 7);
        var typed = CommandResult<Guid>.FromError(kind, "why", 7);

        plain.IsSuccess.Should().BeFalse();
        plain.ErrorKind.Should().Be(kind);
        plain.ErrorMessage.Should().Be("why");
        plain.ErrorCode.Should().Be(7);
        typed.ErrorKind.Should().Be(kind);
        typed.Value.Should().Be(Guid.Empty);
    }

    [Fact]
    public void FromError_WithKindNone_IsRejected()
    {
        var plain = () => CommandResult.FromError(CommandErrorKind.None, "why");
        var typed = () => CommandResult<int>.FromError(CommandErrorKind.None, "why");

        plain.Should().Throw<ArgumentOutOfRangeException>();
        typed.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void KindFactories_ProduceTheirKind()
    {
        CommandResult.NotFound("x").ErrorKind.Should().Be(CommandErrorKind.NotFound);
        CommandResult.Conflict("x").ErrorKind.Should().Be(CommandErrorKind.Conflict);
        CommandResult.Unauthorized("x").ErrorKind.Should().Be(CommandErrorKind.Unauthorized);
        CommandResult.Forbidden("x").ErrorKind.Should().Be(CommandErrorKind.Forbidden);
        CommandResult.Unavailable("x", 503).ErrorCode.Should().Be(503);

        CommandResult<string>.NotFound("x").Should().BeOfType<CommandResult<string>>().Which.ErrorKind.Should().Be(CommandErrorKind.NotFound);
        CommandResult<string>.Conflict("x").ErrorKind.Should().Be(CommandErrorKind.Conflict);
        CommandResult<string>.Unauthorized("x").ErrorKind.Should().Be(CommandErrorKind.Unauthorized);
        CommandResult<string>.Forbidden("x").ErrorKind.Should().Be(CommandErrorKind.Forbidden);
        CommandResult<string>.Unavailable("x").ErrorKind.Should().Be(CommandErrorKind.Unavailable);
        CommandResult<string>.NotFound("x").Value.Should().BeNull();
    }

    [Fact]
    public void Invalid_CarriesTheFailuresAsDataWithAFixedSummary()
    {
        var failure = new ValidationFailure("QUANTITY_RANGE", "Quantity must be positive.", "Quantity");

        var result = CommandResult.Invalid(failure);

        result.IsSuccess.Should().BeFalse();
        result.ErrorKind.Should().Be(CommandErrorKind.Validation);
        result.ErrorMessage.Should().Be("Validation failed.");
        result.ValidationFailures.Should().ContainSingle().Which.Should().Be(failure);
    }

    [Fact]
    public void Invalid_WithListAndMessage_UsesThem()
    {
        var failures = new List<ValidationFailure> { new("A", "a"), new("B", "b", "Member") };

        var plain = CommandResult.Invalid(failures, "Fix the input.", 42);
        var typed = CommandResult<int>.Invalid(failures, "Fix the input.");

        plain.ValidationFailures.Should().Equal(failures);
        plain.ErrorMessage.Should().Be("Fix the input.");
        plain.ErrorCode.Should().Be(42);
        typed.ErrorKind.Should().Be(CommandErrorKind.Validation);
        typed.ValidationFailures.Should().Equal(failures);
        typed.ErrorMessage.Should().Be("Fix the input.");
    }

    [Fact]
    public void Invalid_WithNullList_Throws()
    {
        var plain = () => CommandResult.Invalid((IReadOnlyList<ValidationFailure>)null!);
        var typed = () => CommandResult<int>.Invalid((IReadOnlyList<ValidationFailure>)null!);

        plain.Should().Throw<ArgumentNullException>();
        typed.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NormalizesTheKind()
    {
        // A success never carries a kind; a failure given None becomes a plain Failure (a deserialized payload may say either).
        new CommandResult<int>(true, 1, CommandErrorKind.NotFound, null, null, null).ErrorKind.Should().Be(CommandErrorKind.None);
        new CommandResult<int>(false, 0, CommandErrorKind.None, "x", null, null).ErrorKind.Should().Be(CommandErrorKind.Failure);
        new CommandResult<int>(false, 0, CommandErrorKind.Validation, "x", null, null).ValidationFailures.Should().BeEmpty();
    }

    [Fact]
    public void Equality_ComparesValidationFailuresByValue()
    {
        var a = CommandResult.Invalid(new ValidationFailure("A", "a", "M"));
        var b = CommandResult.Invalid(new ValidationFailure("A", "a", "M"));
        var c = CommandResult.Invalid(new ValidationFailure("A", "a", "Other"));

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
        a.Should().NotBe(c);
        CommandResult.NotFound("x").Should().NotBe(CommandResult.Conflict("x"), "a different kind is a different outcome");
        CommandResult<int>.Invalid(new ValidationFailure("A", "a")).Should().Be(CommandResult<int>.Invalid(new ValidationFailure("A", "a")));
    }

    [Fact]
    public void Equality_TwoSuccessResults_AreEqualByValue()
    {
        var a = CommandResult.FromSuccess();
        var b = CommandResult.FromSuccess();

        // record value semantics: distinct instances, equal by value.
        a.Should().NotBeSameAs(b);
        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equality_TwoIdenticalErrorResults_AreEqualByValue()
    {
        var a = CommandResult.FromError("oops", 500);
        var b = CommandResult.FromError("oops", 500);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Equality_SuccessAndFailure_AreNotEqual()
    {
        var success = CommandResult.FromSuccess();
        var failure = CommandResult.FromError("oops", 500);

        success.Should().NotBe(failure);
        (success != failure).Should().BeTrue();
    }

    [Fact]
    public void Equality_FailuresDifferingOnlyByCode_AreNotEqual()
    {
        var a = CommandResult.FromError("oops", 500);
        var b = CommandResult.FromError("oops", 400);

        a.Should().NotBe(b);
    }

    [Fact]
    public void Equality_FailuresDifferingOnlyByMessage_AreNotEqual()
    {
        var a = CommandResult.FromError("first");
        var b = CommandResult.FromError("second");

        a.Should().NotBe(b);
    }
}
