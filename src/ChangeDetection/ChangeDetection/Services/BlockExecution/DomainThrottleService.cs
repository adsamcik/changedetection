using System.Collections.Concurrent;

namespace ChangeDetection.Services.BlockExecution;

/// <summary>
/// Per-domain rate limiting to prevent hammering remote servers.
/// Enforces a minimum delay between requests to the same domain and applies
/// exponential backoff when a domain returns 429 Too Many Requests.
/// </summary>
public interface IDomainThrottleService
{
    /// <summary>Delays until the domain's cooldown window has elapsed.</summary>
    Task WaitForSlotAsync(string domain, CancellationToken ct);

    /// <summary>Records a 429 response, putting the domain into an extended cooldown.</summary>
    void Record429(string domain, TimeSpan? retryAfter);
}

public class DomainThrottleService : IDomainThrottleService
{
    private static readonly TimeSpan MinDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DefaultBackoff = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DomainState> _states = new(StringComparer.OrdinalIgnoreCase);

    public async Task WaitForSlotAsync(string domain, CancellationToken ct)
    {
        var state = _states.GetOrAdd(domain, _ => new DomainState());

        DateTimeOffset nextAllowed;
        lock (state)
        {
            var cooldown = state.BackoffUntil > DateTimeOffset.UtcNow
                ? state.BackoffUntil - DateTimeOffset.UtcNow
                : MinDelay;

            nextAllowed = state.LastRequestTime + cooldown;
        }

        var now = DateTimeOffset.UtcNow;
        if (nextAllowed > now)
        {
            await Task.Delay(nextAllowed - now, ct);
        }

        lock (state)
        {
            state.LastRequestTime = DateTimeOffset.UtcNow;
        }
    }

    public void Record429(string domain, TimeSpan? retryAfter)
    {
        var state = _states.GetOrAdd(domain, _ => new DomainState());

        lock (state)
        {
            state.Consecutive429Count++;

            // Use server-supplied Retry-After if available, otherwise escalate
            var backoff = retryAfter
                ?? TimeSpan.FromSeconds(DefaultBackoff.TotalSeconds * Math.Pow(2, state.Consecutive429Count - 1));

            if (backoff > MaxBackoff) backoff = MaxBackoff;

            state.BackoffUntil = DateTimeOffset.UtcNow + backoff;
        }
    }

    private sealed class DomainState
    {
        public DateTimeOffset LastRequestTime = DateTimeOffset.MinValue;
        public DateTimeOffset BackoffUntil = DateTimeOffset.MinValue;
        public int Consecutive429Count;
    }
}
