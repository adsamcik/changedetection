using ChangeDetection.Services.BlockExecution;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

/// <summary>
/// Tests for the DomainThrottleService which enforces per-domain rate limiting
/// and exponential backoff on 429 responses.
/// </summary>
[Category("Unit")]
public class DomainThrottleTests
{
    [Test]
    public async Task FirstRequest_ToDomain_NoDelay()
    {
        // Arrange
        var sut = new DomainThrottleService();

        // Act: measure how long the first request takes
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sut.WaitForSlotAsync("example.com", CancellationToken.None);
        sw.Stop();

        // Assert: first request should complete with no meaningful delay
        // (allow up to 100ms for test overhead / scheduling)
        sw.ElapsedMilliseconds.ShouldBeLessThan(100,
            "First request to a new domain should not be delayed");
    }

    [Test]
    public async Task SecondRequest_Within500ms_IsDelayed()
    {
        // Arrange
        var sut = new DomainThrottleService();
        const string domain = "delayed-domain.com";

        // First request — primes the domain state
        await sut.WaitForSlotAsync(domain, CancellationToken.None);

        // Act: immediately make second request and measure delay
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sut.WaitForSlotAsync(domain, CancellationToken.None);
        sw.Stop();

        // Assert: MinDelay is 500ms, so second request should be delayed ~500ms
        // Allow tolerance for timing jitter (350ms+ is close enough)
        sw.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(350,
            "Second request within the rate limit window should be delayed by ~500ms");
    }

    [Test]
    public async Task Record429_PutsDomainIntoCooldown()
    {
        // Arrange
        var sut = new DomainThrottleService();
        const string domain = "rate-limited.com";

        // Prime the domain
        await sut.WaitForSlotAsync(domain, CancellationToken.None);

        // Record a 429 — should trigger extended backoff (default 10s)
        sut.Record429(domain, null);

        // Act: next request should be delayed by the backoff period
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await sut.WaitForSlotAsync(domain, cts.Token);
        sw.Stop();

        // Assert: default backoff is 10s for first 429
        sw.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(8_000,
            "429 response should trigger extended cooldown (default ~10s)");
    }

    [Test]
    public async Task Record429_WithExplicitRetryAfter_UsesServerValue()
    {
        // Arrange
        var sut = new DomainThrottleService();
        const string domain = "retry-after.com";

        await sut.WaitForSlotAsync(domain, CancellationToken.None);

        // Record a 429 with explicit 2-second Retry-After
        sut.Record429(domain, TimeSpan.FromSeconds(2));

        // Act
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sut.WaitForSlotAsync(domain, cts.Token);
        sw.Stop();

        // Assert: should respect the 2-second Retry-After header
        sw.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(1_500,
            "Should respect server-supplied Retry-After");
        sw.ElapsedMilliseconds.ShouldBeLessThan(5_000,
            "Should not overshoot the 2-second Retry-After by much");
    }

    [Test]
    public async Task DifferentDomains_AreIndependent()
    {
        // Arrange
        var sut = new DomainThrottleService();
        const string domainA = "domain-a.com";
        const string domainB = "domain-b.com";

        // Prime domain A and record a 429
        await sut.WaitForSlotAsync(domainA, CancellationToken.None);
        sut.Record429(domainA, TimeSpan.FromSeconds(5));

        // Act: domain B should NOT be affected by domain A's 429
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sut.WaitForSlotAsync(domainB, CancellationToken.None);
        sw.Stop();

        // Assert: domain B should have no delay (first request)
        sw.ElapsedMilliseconds.ShouldBeLessThan(100,
            "Different domains should have independent throttle state");
    }

    [Test]
    public async Task Consecutive429s_EscalateBackoff()
    {
        // Arrange
        var sut = new DomainThrottleService();
        const string domain = "backoff-escalation.com";

        await sut.WaitForSlotAsync(domain, CancellationToken.None);

        // Record multiple 429s — backoff should escalate exponentially
        sut.Record429(domain, null); // First: 10s
        sut.Record429(domain, null); // Second: 20s
        sut.Record429(domain, null); // Third: 40s

        // Act: the backoff should be significantly longer than the base 10s
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await sut.WaitForSlotAsync(domain, cts.Token);
        sw.Stop();

        // Assert: after 3 consecutive 429s, delay should be well above 10s
        // Base 10s × 2^2 = 40s for the third escalation
        sw.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(30_000,
            "Consecutive 429s should escalate the backoff exponentially");
    }

    [Test]
    public async Task CancellationToken_IsRespected()
    {
        // Arrange
        var sut = new DomainThrottleService();
        const string domain = "cancel-test.com";

        await sut.WaitForSlotAsync(domain, CancellationToken.None);
        sut.Record429(domain, TimeSpan.FromSeconds(30)); // Long cooldown

        // Act & Assert: cancellation should abort the wait
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Should.ThrowAsync<OperationCanceledException>(
            () => sut.WaitForSlotAsync(domain, cts.Token));
    }

    [Test]
    public async Task DomainComparison_IsCaseInsensitive()
    {
        // Arrange
        var sut = new DomainThrottleService();

        // Prime with lowercase
        await sut.WaitForSlotAsync("example.com", CancellationToken.None);

        // Act: request with uppercase variant — should share state
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await sut.WaitForSlotAsync("EXAMPLE.COM", CancellationToken.None);
        sw.Stop();

        // Assert: should be delayed because it's the same domain (case-insensitive)
        sw.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(350,
            "Domain comparison should be case-insensitive");
    }
}
