using CQRSharp.Abstractions.Interfaces.Notifications;
using CQRSharp.Core.Background.Outbox.Types;
using FluentAssertions;

namespace CQRSharp.Tests.Core;

/// <summary>
///     Tests for the request-scoped in-memory <see cref="Outbox" /> (<c>IOutbox</c>): it collects notifications during
///     a request scope and is drained once so another component can persist them.
/// </summary>
public sealed class OutboxTests
{
    private sealed record Ping : INotification;

    private sealed record Pong : INotification;

    [Fact(DisplayName = "Outbox: Add then GetNotifications returns them in order")]
    public void Add_Then_GetNotifications_ReturnsInOrder()
    {
        var outbox = new Outbox();
        var a = new Ping();
        var b = new Pong();

        outbox.Add(a);
        outbox.Add(b);

        outbox.GetNotifications().Should().Equal(a, b);
    }

    [Fact(DisplayName = "Outbox: Drain returns the collected notifications and clears the buffer")]
    public void Drain_ReturnsAndClears()
    {
        var outbox = new Outbox();
        var a = new Ping();
        outbox.Add(a);

        outbox.Drain().Should().Equal(a);
        outbox.GetNotifications().Should().BeEmpty("Drain must clear the buffer");
        outbox.Drain().Should().BeEmpty("a second Drain returns nothing");
    }

    [Fact(DisplayName = "Outbox: Drain on an empty outbox returns an empty list")]
    public void Drain_Empty_ReturnsEmpty()
    {
        new Outbox().Drain().Should().BeEmpty();
    }

    [Fact(DisplayName = "Outbox: Add(null) throws ArgumentNullException")]
    public void Add_Null_Throws()
    {
        var outbox = new Outbox();
        var act = () => outbox.Add(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
