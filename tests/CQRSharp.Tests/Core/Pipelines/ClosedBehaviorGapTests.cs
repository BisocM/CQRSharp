using CQRSharp.Core.Pipelines;
using CQRSharp.Pipelines;
using FluentAssertions;

namespace CQRSharp.Tests.Core;

/// <summary>
///     The gap a generated module records for an open-generic behavior it could not close over a value-type-result
///     request or a value-type notification: matched against a registration by the behavior's runtime name, and by the
///     target's type or name.
/// </summary>
public sealed class ClosedBehaviorGapTests
{
    [Fact(DisplayName = "A gap for a nameable request matches its behavior registration for that request only")]
    public void Gap_by_service_type()
    {
        var gap = new ClosedBehaviorGap(typeof(IPipelineBehavior<GapQuery, int>), typeof(GapBehavior<,>).FullName!, "CQRSharp.Tests", "it is nested in a generic type");

        gap.Matches(typeof(IPipelineBehavior<GapQuery, int>), typeof(GapBehavior<,>)).Should().BeTrue();
        gap.Matches(typeof(IPipelineBehavior<OtherGapQuery, int>), typeof(GapBehavior<,>)).Should().BeFalse();
        gap.Matches(typeof(IPipelineBehavior<GapQuery, int>), typeof(OtherGapBehavior<,>)).Should().BeFalse();
    }

    [Fact(DisplayName = "A gap for a request generated code cannot name matches by the request's runtime name and kind")]
    public void Gap_by_request_name()
    {
        var gap = new ClosedBehaviorGap(typeof(IPipelineBehavior<,>), typeof(GapQuery).FullName!, "CQRSharp.Tests", typeof(GapBehavior<,>).FullName!, "CQRSharp.Tests", "the request is internal");

        gap.Matches(typeof(IPipelineBehavior<GapQuery, int>), typeof(GapBehavior<,>)).Should().BeTrue();
        gap.Matches(typeof(IStreamPipelineBehavior<GapQuery, int>), typeof(GapBehavior<,>)).Should().BeFalse("a stream gap and a request gap are different pipelines");
        gap.Matches(typeof(IPipelineBehavior<GapQuery, int>), typeof(OtherGapBehavior<,>)).Should().BeFalse();
    }

    [Fact(DisplayName = "The error names the behavior, the request and the reason")]
    public void Gap_exception()
    {
        var gap = new ClosedBehaviorGap(typeof(IPipelineBehavior<GapQuery, int>), "Lib.TenantBehavior`2", "Lib", "it is internal to 'Lib'");

        gap.ToException().Message.Should().Contain("Lib.TenantBehavior`2").And.Contain(typeof(GapQuery).FullName!).And.Contain("because it is internal to 'Lib'");
    }

    [Fact(DisplayName = "A gap for a value-type notification matches its notification behavior registration, by type or by name")]
    public void Gap_for_a_notification()
    {
        var byType = new ClosedBehaviorGap(typeof(INotificationPipelineBehavior<GapNotification>), typeof(GapNotificationBehavior<>).FullName!, "CQRSharp.Tests", "it is private");
        var byName = new ClosedBehaviorGap(typeof(INotificationPipelineBehavior<>), typeof(GapNotification).FullName!, "CQRSharp.Tests", typeof(GapNotificationBehavior<>).FullName!, "CQRSharp.Tests", "it is private");

        foreach (var gap in new[] { byType, byName })
        {
            gap.Matches(typeof(INotificationPipelineBehavior<GapNotification>), typeof(GapNotificationBehavior<>)).Should().BeTrue();
            gap.Matches(typeof(INotificationPipelineBehavior<OtherGapNotification>), typeof(GapNotificationBehavior<>)).Should().BeFalse();
        }

        byType.ToException().Message.Should().Contain("notification '" + typeof(GapNotification).FullName).And.Contain("which is a value type")
            .And.Contain("because it is private");
    }

    [Fact(DisplayName = "A gap is refused for a service type that is not a closed behavior interface, or a definition that is not open")]
    public void Gap_arguments_are_checked()
    {
        var closedDefinition = () => new ClosedBehaviorGap(typeof(INotificationPipelineBehavior<GapNotification>), "N", "A", "B", "A", "r");
        var openService = () => new ClosedBehaviorGap(typeof(INotificationPipelineBehavior<>), "B", "A", "r");

        closedDefinition.Should().Throw<ArgumentException>();
        openService.Should().Throw<ArgumentException>();
    }

    // Only their types are needed: never dispatched, so no handler (and the generator does not see private types).
    private sealed class GapQuery : QueryBase<int>;

    private sealed class OtherGapQuery : QueryBase<int>;

    private sealed class GapBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
    {
        public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
    }

    private sealed class OtherGapBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult> where TRequest : IRequest
    {
        public Task<TResult> Handle(TRequest request, RequestHandlerDelegate<TResult> next, CancellationToken cancellationToken) => next(cancellationToken);
    }

    private readonly struct GapNotification : INotification;

    private readonly struct OtherGapNotification : INotification;

    private sealed class GapNotificationBehavior<TNotification> : INotificationPipelineBehavior<TNotification> where TNotification : INotification
    {
        public Task Handle(TNotification notification, NotificationHandlerDelegate next, CancellationToken cancellationToken) => next(cancellationToken);
    }
}
