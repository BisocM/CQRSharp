using System.Threading.Channels;
using CQRSharp.Abstractions.Models.Commands;
using CQRSharp.Core.Notifications.Types;
using CQRSharp.Tests.Shared;
using FluentAssertions;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Unit tests covering <see cref="CommandResult" /> (every factory/branch) and the lifecycle/failure
///     notification types under CQRSharp.Core.Notifications.Types. The notification tests construct each
///     type with realistic values and assert that its properties round-trip, in particular preserving any
///     exception or request/command reference.
/// </summary>
public sealed class CommandResultAndNotificationTests
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
    public void FromError_ToString_DescribesFailureWithMessageAndCode()
    {
        var result = CommandResult.FromError("not found", 404);

        result.ToString().Should().Be("Command failed: not found (Code: 404)");
    }

    [Fact]
    public void FromError_ToString_WithoutCode_RendersEmptyCodeSegment()
    {
        var result = CommandResult.FromError("boom");

        // ErrorCode is null, so the interpolated "(Code: )" segment is empty.
        result.ToString().Should().Be("Command failed: boom (Code: )");
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

    // ---------------------------------------------------------------------
    // Command lifecycle notifications
    // ---------------------------------------------------------------------

    [Fact]
    public void CommandInitiatedNotification_RoundTripsCommandAndName()
    {
        var command = new TestCommand();

        var notification = new CommandInitiatedNotification(command);

        notification.Command.Should().BeSameAs(command);
        notification.CommandName.Should().Be(nameof(TestCommand));
    }

    [Fact]
    public void CommandCompletedNotification_RoundTripsCommandNameAndResult()
    {
        var command = new TestCommand();
        var result = CommandResult.FromSuccess();

        var notification = new CommandCompletedNotification(command, result);

        notification.Command.Should().BeSameAs(command);
        notification.CommandName.Should().Be(nameof(TestCommand));
        notification.Result.Should().BeSameAs(result);
    }

    [Fact]
    public void CommandCompletedNotification_CarriesFailureResult()
    {
        var command = new TestCommand();
        var result = CommandResult.FromError("nope", 409);

        var notification = new CommandCompletedNotification(command, result);

        notification.Result.IsSuccess.Should().BeFalse();
        notification.Result.ErrorMessage.Should().Be("nope");
        notification.Result.ErrorCode.Should().Be(409);
    }

    [Fact]
    public void CommandFailedNotification_PreservesCommandNameAndException()
    {
        var command = new TestCommand();
        var exception = new InvalidOperationException("handler blew up");

        var notification = new CommandFailedNotification(command, exception);

        notification.Command.Should().BeSameAs(command);
        notification.CommandName.Should().Be(nameof(TestCommand));
        notification.Exception.Should().BeSameAs(exception);
        notification.Exception.Message.Should().Be("handler blew up");
    }

    // ---------------------------------------------------------------------
    // Query lifecycle notifications
    // ---------------------------------------------------------------------

    [Fact]
    public void QueryInitiatedNotification_RoundTripsQueryAndName()
    {
        var query = new TestQuery();

        var notification = new QueryInitiatedNotification<TestQueryResult>(query);

        notification.Query.Should().BeSameAs(query);
        notification.QueryName.Should().Be(nameof(TestQuery));
    }

    [Fact]
    public void QueryCompletedNotification_RoundTripsQueryNameAndResult()
    {
        var query = new TestQuery();
        var result = new TestQueryResult("answer");

        var notification = new QueryCompletedNotification<TestQueryResult>(query, result);

        notification.Query.Should().BeSameAs(query);
        notification.QueryName.Should().Be(nameof(TestQuery));
        notification.Result.Should().BeSameAs(result);
    }

    [Fact]
    public void QueryCompletedNotification_AllowsNullResult()
    {
        var query = new TestQuery();

        var notification = new QueryCompletedNotification<TestQueryResult>(query, null);

        notification.Result.Should().BeNull();
    }

    [Fact]
    public void QueryFailedNotification_PreservesQueryNameAndException()
    {
        var query = new TestQuery();
        var exception = new TimeoutException("query timed out");

        var notification = new QueryFailedNotification<TestQueryResult>(query, exception);

        notification.Query.Should().BeSameAs(query);
        notification.QueryName.Should().Be(nameof(TestQuery));
        notification.Exception.Should().BeSameAs(exception);
        notification.Exception.Message.Should().Be("query timed out");
    }

    // ---------------------------------------------------------------------
    // Stream lifecycle notifications
    // ---------------------------------------------------------------------

    [Fact]
    public void StreamInitiatedNotification_RoundTripsRequestAndName()
    {
        var request = new TestStreamRequest(3);

        var notification = new StreamInitiatedNotification<int>(request);

        notification.Request.Should().BeSameAs(request);
        notification.RequestName.Should().Be(nameof(TestStreamRequest));
    }

    [Fact]
    public void StreamCompletedNotification_RoundTripsRequestNameAndItemCount()
    {
        var request = new TestStreamRequest(5);

        var notification = new StreamCompletedNotification<int>(request, 5);

        notification.Request.Should().BeSameAs(request);
        notification.RequestName.Should().Be(nameof(TestStreamRequest));
        notification.ItemsYielded.Should().Be(5);
    }

    [Fact]
    public void StreamFailedNotification_PreservesRequestItemCountAndException()
    {
        var request = new TestStreamRequest(10);
        var exception = new InvalidOperationException("stream faulted");

        var notification = new StreamFailedNotification<int>(request, 7, exception);

        notification.Request.Should().BeSameAs(request);
        notification.RequestName.Should().Be(nameof(TestStreamRequest));
        notification.ItemsYielded.Should().Be(7);
        notification.Exception.Should().BeSameAs(exception);
        notification.Exception.Message.Should().Be("stream faulted");
    }

    // ---------------------------------------------------------------------
    // Background task notifications
    // ---------------------------------------------------------------------

    [Fact]
    public void TaskEnqueuedNotification_RoundTripsSequenceNumberAndWorkItem()
    {
        Func<CancellationToken, Task> workItem = _ => Task.CompletedTask;

        var notification = new TaskEnqueuedNotification(42, workItem);

        notification.SequenceNumber.Should().Be(42);
        notification.WorkItem.Should().BeSameAs(workItem);
    }

    [Fact]
    public void TaskEnqueuedNotification_NullWorkItem_Throws()
    {
        var act = () => new TaskEnqueuedNotification(1, null!);

        act.Should().Throw<ArgumentNullException>()
            .Which.ParamName.Should().Be("workItem");
    }

    [Fact]
    public void TaskRejectedNotification_RoundTripsSequenceNumberAndFullMode()
    {
        var notification = new TaskRejectedNotification(7, BoundedChannelFullMode.DropWrite);

        notification.SequenceNumber.Should().Be(7);
        notification.FullMode.Should().Be(BoundedChannelFullMode.DropWrite);
    }
}