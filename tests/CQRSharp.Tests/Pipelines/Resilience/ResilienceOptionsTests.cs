using CQRSharp.Pipelines;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CQRSharp.Tests.Pipelines;

/// <summary>
///     The retry schedule <see cref="ResilienceOptions" /> describes, its documented defaults, and the start-up
///     validation that rejects a schedule it cannot honour instead of quietly reinterpreting it.
/// </summary>
public class ResilienceOptionsTests
{
    [Theory(DisplayName = "Non-positive retry attempt yields zero delay")]
    [InlineData(0)]
    [InlineData(-1)]
    public void ComputeRetryDelay_NonPositiveAttempt_ReturnsZero(int attempt)
    {
        var options = new ResilienceOptions { BaseDelay = TimeSpan.FromSeconds(5) };

        options.ComputeRetryDelay(attempt).Should().Be(TimeSpan.Zero);
    }

    [Fact(DisplayName = "A zero base delay retries immediately")]
    public void ComputeRetryDelay_ZeroBaseDelay_ReturnsZero()
    {
        var options = new ResilienceOptions { BaseDelay = TimeSpan.Zero };

        options.ComputeRetryDelay(1).Should().Be(TimeSpan.Zero);
        options.ComputeRetryDelay(3).Should().Be(TimeSpan.Zero);
    }

    [Fact(DisplayName = "A multiplier of 1 keeps every delay at BaseDelay")]
    public void ComputeRetryDelay_FixedDelay()
    {
        var options = new ResilienceOptions { BaseDelay = TimeSpan.FromMilliseconds(200), MaxDelay = TimeSpan.FromMinutes(10) };

        options.ComputeRetryDelay(1).Should().Be(TimeSpan.FromMilliseconds(200));
        options.ComputeRetryDelay(4).Should().Be(TimeSpan.FromMilliseconds(200));
    }

    [Fact(DisplayName = "Exponential backoff grows by the multiplier per attempt")]
    public void ComputeRetryDelay_ExponentialGrowth()
    {
        var options = new ResilienceOptions
        {
            BaseDelay = TimeSpan.FromMilliseconds(100),
            BackoffMultiplier = 2.0,
            MaxDelay = TimeSpan.FromMinutes(10)
        };

        options.ComputeRetryDelay(1).Should().Be(TimeSpan.FromMilliseconds(100));
        options.ComputeRetryDelay(2).Should().Be(TimeSpan.FromMilliseconds(200));
        options.ComputeRetryDelay(3).Should().Be(TimeSpan.FromMilliseconds(400));
        options.ComputeRetryDelay(4).Should().Be(TimeSpan.FromMilliseconds(800));
    }

    [Fact(DisplayName = "Backoff is capped at MaxDelay, even when the exponential overflows")]
    public void ComputeRetryDelay_CappedAtMaxDelay()
    {
        var options = new ResilienceOptions
        {
            BaseDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 10.0,
            MaxDelay = TimeSpan.FromSeconds(5)
        };

        options.ComputeRetryDelay(3).Should().Be(TimeSpan.FromSeconds(5));
        options.ComputeRetryDelay(int.MaxValue).Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "Default ResilienceOptions match the documented defaults")]
    public void Defaults_AreAsDocumented()
    {
        var options = new ResilienceOptions();

        options.MaxRetries.Should().Be(3);
        options.BaseDelay.Should().Be(TimeSpan.FromSeconds(1));
        options.BackoffMultiplier.Should().Be(1.0);
        options.MaxDelay.Should().Be(TimeSpan.FromSeconds(30));
        options.ComputeRetryDelay(5).Should().Be(TimeSpan.FromSeconds(1));
    }

    [Theory(DisplayName = "A backoff multiplier below 1 or not finite is rejected at start")]
    [InlineData(0.5)]
    [InlineData(-3.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_multiplier_is_rejected(double multiplier)
    {
        var act = () => Resolve(o => o.BackoffMultiplier = multiplier);

        act.Should().Throw<OptionsValidationException>().WithMessage("*BackoffMultiplier must be a finite number of at least 1*");
    }

    [Fact(DisplayName = "A MaxDelay shorter than BaseDelay is rejected at start")]
    public void MaxDelay_below_BaseDelay_is_rejected()
    {
        var act = () => Resolve(o =>
        {
            o.BaseDelay = TimeSpan.FromSeconds(2);
            o.MaxDelay = TimeSpan.Zero;
        });

        act.Should().Throw<OptionsValidationException>().WithMessage("*MaxDelay must be at least BaseDelay*");
    }

    [Fact(DisplayName = "A negative retry count is rejected at start")]
    public void Negative_MaxRetries_is_rejected()
    {
        var act = () => Resolve(o => o.MaxRetries = -1);

        act.Should().Throw<OptionsValidationException>().WithMessage("*MaxRetries must not be negative*");
    }

    private static ResilienceOptions Resolve(Action<ResilienceOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddCqrsGenerated(b => b.UseResilience(configure));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ResilienceOptions>>().Value;
    }
}
