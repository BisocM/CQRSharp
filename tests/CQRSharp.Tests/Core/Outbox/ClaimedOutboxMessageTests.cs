using CQRSharp.Persistence;
using FluentAssertions;

namespace CQRSharp.Tests.Core;

/// <summary>
///     A claimed message always carries a claim on itself: the constructor is where a broken custom store fails, so the
///     processor never has to trust one.
/// </summary>
public sealed class ClaimedOutboxMessageTests
{
    private static readonly DateTime LeasedUntil = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact(DisplayName = "A message and a claim on it are kept as they are")]
    public void Valid_pair_is_kept()
    {
        var message = Message();
        var claim = new OutboxClaim(message.Id, "token", LeasedUntil);

        var claimed = new ClaimedOutboxMessage(message, claim);

        claimed.Message.Should().BeSameAs(message);
        claimed.Claim.Should().Be(claim);
    }

    [Fact(DisplayName = "A null message is rejected")]
    public void Null_message_is_rejected()
    {
        var act = () => new ClaimedOutboxMessage(null!, new OutboxClaim(Guid.NewGuid(), "token", LeasedUntil));

        act.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("message");
    }

    [Theory(DisplayName = "A claim without a token is rejected")]
    [InlineData("")]
    [InlineData(null)]
    public void Claim_without_a_token_is_rejected(string? token)
    {
        var message = Message();

        var act = () => new ClaimedOutboxMessage(message, new OutboxClaim(message.Id, token!, LeasedUntil));

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("claim");
    }

    [Fact(DisplayName = "A default claim, whose token is null, is rejected")]
    public void Default_claim_is_rejected()
    {
        var act = () => new ClaimedOutboxMessage(Message(), default);

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("claim");
    }

    [Fact(DisplayName = "A claim on another message is rejected")]
    public void Claim_on_another_message_is_rejected()
    {
        var act = () => new ClaimedOutboxMessage(Message(), new OutboxClaim(Guid.NewGuid(), "token", LeasedUntil));

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("claim");
    }

    private static OutboxMessage Message()
        => new(Guid.NewGuid(), "claimed.test", "Tests.Handler", "{}"u8.ToArray(), LeasedUntil.AddMinutes(-10),
            OutboxMessageStatus.InProgress, null, null, 0);
}
