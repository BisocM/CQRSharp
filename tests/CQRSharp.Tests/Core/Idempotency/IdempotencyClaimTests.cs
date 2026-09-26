using CQRSharp.Persistence;
using FluentAssertions;

namespace CQRSharp.Tests.Core;

/// <summary>
///     A won claim always carries the token that identifies it: <see cref="IdempotencyClaim.ClaimedWith" /> is where a
///     broken custom store fails, so the behavior never has to trust one.
/// </summary>
public sealed class IdempotencyClaimTests
{
    [Fact(DisplayName = "A claim with a token is claimed and keeps its token")]
    public void Claim_keeps_its_token()
    {
        var claim = IdempotencyClaim.ClaimedWith("t");

        claim.IsClaimed.Should().BeTrue();
        claim.Token.Should().Be("t");
    }

    [Theory(DisplayName = "A claim without a token is rejected")]
    [InlineData("")]
    [InlineData(null)]
    public void Claim_without_a_token_is_rejected(string? token)
    {
        var act = () => IdempotencyClaim.ClaimedWith(token!);

        act.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("token");
    }
}
