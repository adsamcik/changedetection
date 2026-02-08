namespace ChangeDetection.Services.AutoHealing;

/// <summary>
/// Exponential backoff and retry utilities for resilient pipeline execution.
/// </summary>
public static class ResiliencePolicy
{
    /// <summary>
    /// Calculates exponential backoff with jitter (±25%).
    /// </summary>
    public static TimeSpan CalculateBackoff(int attempt, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null)
    {
        var @base = baseDelay ?? TimeSpan.FromSeconds(1);
        var max = maxDelay ?? TimeSpan.FromMinutes(5);
        var delay = TimeSpan.FromMilliseconds(@base.TotalMilliseconds * Math.Pow(2, attempt));

        // Add jitter (±25%)
        var jitter = delay.TotalMilliseconds * (Random.Shared.NextDouble() * 0.5 - 0.25);
        delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds + jitter);

        return delay > max ? max : delay;
    }

    /// <summary>
    /// Parses a Retry-After header value (seconds or HTTP-date format).
    /// </summary>
    public static TimeSpan? ParseRetryAfter(string? retryAfterValue)
    {
        if (string.IsNullOrEmpty(retryAfterValue))
            return null;

        if (int.TryParse(retryAfterValue, out var seconds))
            return TimeSpan.FromSeconds(seconds);

        if (DateTimeOffset.TryParse(retryAfterValue, out var date))
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }
}
