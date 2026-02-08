using ChangeDetection.Services.AutoHealing;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.AutoHealing;

[Category("Unit")]
public class ResiliencePolicyTests
{
    [Test]
    public async Task CalculateBackoff_IncreasesExponentially()
    {
        var baseDelay = TimeSpan.FromSeconds(1);
        var delay0 = ResiliencePolicy.CalculateBackoff(0, baseDelay);
        var delay1 = ResiliencePolicy.CalculateBackoff(1, baseDelay);
        var delay2 = ResiliencePolicy.CalculateBackoff(2, baseDelay);

        // With jitter ±25%, delay0 ≈ 1s (0.75–1.25), delay1 ≈ 2s (1.5–2.5), delay2 ≈ 4s (3–5)
        delay0.TotalMilliseconds.ShouldBeInRange(750, 1250);
        delay1.TotalMilliseconds.ShouldBeInRange(1500, 2500);
        delay2.TotalMilliseconds.ShouldBeInRange(3000, 5000);

        delay1.ShouldBeGreaterThan(delay0);
        delay2.ShouldBeGreaterThan(delay1);

        await Task.CompletedTask;
    }

    [Test]
    public async Task CalculateBackoff_CapsAtMaxDelay()
    {
        var maxDelay = TimeSpan.FromSeconds(10);
        var delay = ResiliencePolicy.CalculateBackoff(20, TimeSpan.FromSeconds(1), maxDelay);

        delay.ShouldBeLessThanOrEqualTo(maxDelay);

        await Task.CompletedTask;
    }

    [Test]
    public async Task CalculateBackoff_UsesDefaultBaseAndMax()
    {
        var delay = ResiliencePolicy.CalculateBackoff(0);
        // Default base is 1s, so with jitter ≈ 0.75–1.25s
        delay.TotalMilliseconds.ShouldBeInRange(750, 1250);

        var capDelay = ResiliencePolicy.CalculateBackoff(30);
        // Default max is 5 minutes
        capDelay.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(5));

        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseRetryAfter_ParsesSeconds()
    {
        var result = ResiliencePolicy.ParseRetryAfter("120");

        result.ShouldNotBeNull();
        result!.Value.TotalSeconds.ShouldBe(120);

        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseRetryAfter_ParsesDate()
    {
        var futureDate = DateTimeOffset.UtcNow.AddMinutes(5);
        var result = ResiliencePolicy.ParseRetryAfter(futureDate.ToString("R"));

        result.ShouldNotBeNull();
        result!.Value.TotalMinutes.ShouldBeInRange(4.5, 5.5);

        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseRetryAfter_PastDate_ReturnsZero()
    {
        var pastDate = DateTimeOffset.UtcNow.AddMinutes(-5);
        var result = ResiliencePolicy.ParseRetryAfter(pastDate.ToString("R"));

        result.ShouldNotBeNull();
        result!.Value.ShouldBe(TimeSpan.Zero);

        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseRetryAfter_ReturnsNullForNull()
    {
        var result = ResiliencePolicy.ParseRetryAfter(null);
        result.ShouldBeNull();

        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseRetryAfter_ReturnsNullForEmpty()
    {
        var result = ResiliencePolicy.ParseRetryAfter("");
        result.ShouldBeNull();

        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseRetryAfter_ReturnsNullForInvalid()
    {
        var result = ResiliencePolicy.ParseRetryAfter("not-a-number-or-date");
        result.ShouldBeNull();

        await Task.CompletedTask;
    }
}
