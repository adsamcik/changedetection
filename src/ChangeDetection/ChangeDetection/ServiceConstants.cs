namespace ChangeDetection;

/// <summary>
/// Named constants for service configuration defaults and limits.
/// Centralizes magic numbers used across background services, pipelines, and middleware.
/// </summary>
public static class ServiceConstants
{
    // ── Rate Limiting ──────────────────────────────────────────────────

    /// <summary>Global rate limit: max requests per window for any client.</summary>
    public const int GlobalRateLimitPermits = 100;

    /// <summary>LLM endpoint rate limit: max requests per window.</summary>
    public const int LlmRateLimitPermits = 10;

    /// <summary>Watch-check trigger rate limit: max requests per window.</summary>
    public const int WatchCheckRateLimitPermits = 20;

    /// <summary>Sliding window duration shared by all fixed-window rate limiters.</summary>
    public static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);

    // ── Output Caching ─────────────────────────────────────────────────

    /// <summary>Default API response cache duration.</summary>
    public static readonly TimeSpan DefaultCacheExpiration = TimeSpan.FromSeconds(10);

    /// <summary>Longer cache for infrequently-changing API responses.</summary>
    public static readonly TimeSpan LongCacheExpiration = TimeSpan.FromMinutes(5);

    /// <summary>Cache duration for category endpoint responses.</summary>
    public static readonly TimeSpan CategoryCacheExpiration = TimeSpan.FromSeconds(30);

    /// <summary>Short cache duration for frequently-changing list endpoints (watches, changes).</summary>
    public static readonly TimeSpan ShortListCacheExpiration = TimeSpan.FromSeconds(5);

    /// <summary>Cache duration for individual change/watch detail endpoints.</summary>
    public static readonly TimeSpan DetailCacheExpiration = TimeSpan.FromSeconds(10);

    // ── SignalR ─────────────────────────────────────────────────────────

    /// <summary>Maximum SignalR message size (64 KB).</summary>
    public const int SignalRMaxMessageSize = 64 * 1024;

    /// <summary>SignalR stream buffer capacity (items).</summary>
    public const int SignalRStreamBufferCapacity = 20;

    /// <summary>Interval between SignalR keep-alive pings.</summary>
    public static readonly TimeSpan SignalRKeepAliveInterval = TimeSpan.FromSeconds(15);

    /// <summary>How long the server waits before considering a client disconnected.</summary>
    public static readonly TimeSpan SignalRClientTimeout = TimeSpan.FromSeconds(30);

    // ── Background Services ────────────────────────────────────────────

    /// <summary>How often the change-check service polls for due watches.</summary>
    public static readonly TimeSpan ChangeCheckInterval = TimeSpan.FromMinutes(1);

    /// <summary>How often the snapshot cleanup service runs.</summary>
    public static readonly TimeSpan SnapshotCleanupInterval = TimeSpan.FromHours(1);

    /// <summary>How often the database backup service runs.</summary>
    public static readonly TimeSpan DatabaseBackupInterval = TimeSpan.FromHours(24);

    /// <summary>Number of database backups to retain during cleanup.</summary>
    public const int DatabaseBackupRetainCount = 7;

    /// <summary>How often the notification outbox processor runs.</summary>
    public static readonly TimeSpan NotificationProcessingInterval = TimeSpan.FromSeconds(10);

    /// <summary>Interval between notification outbox cleanup passes.</summary>
    public static readonly TimeSpan NotificationCleanupInterval = TimeSpan.FromHours(6);

    /// <summary>Age after which sent notifications are purged.</summary>
    public static readonly TimeSpan NotificationCleanupAge = TimeSpan.FromDays(7);

    /// <summary>Timeout for a single notification send attempt.</summary>
    public static readonly TimeSpan NotificationProcessingTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Max pending notifications processed per cycle.</summary>
    public const int NotificationPendingBatchSize = 50;

    /// <summary>Max retry notifications processed per cycle.</summary>
    public const int NotificationRetryBatchSize = 20;

    // ── Pipeline Worker ────────────────────────────────────────────────

    /// <summary>Time before a processing pipeline item is considered stale.</summary>
    public static readonly TimeSpan PipelineStaleThreshold = TimeSpan.FromMinutes(30);

    /// <summary>Interval for checking stale pipeline items.</summary>
    public static readonly TimeSpan PipelineStaleCheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>Maximum retry attempts for transient pipeline failures.</summary>
    public const int PipelineMaxRetryAttempts = 3;

    /// <summary>Default number of pipeline workers when not configured.</summary>
    public const int PipelineDefaultWorkerCount = 1;

    /// <summary>Age after which completed/failed pipeline items are purged.</summary>
    public static readonly TimeSpan PipelineItemPurgeAge = TimeSpan.FromDays(7);

    /// <summary>How often the database maintenance service runs compaction.</summary>
    public static readonly TimeSpan DatabaseMaintenanceInterval = TimeSpan.FromDays(7);

    /// <summary>Database file size warning threshold in megabytes.</summary>
    public const long DatabaseSizeWarningMb = 500;

    /// <summary>Delay before retrying after an unexpected worker error.</summary>
    public static readonly TimeSpan PipelineWorkerRetryDelay = TimeSpan.FromSeconds(5);

    // ── Session Management ─────────────────────────────────────────────

    /// <summary>Idle timeout before a conversation session is expired.</summary>
    public static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(30);

    /// <summary>How often expired sessions are cleaned up.</summary>
    public static readonly TimeSpan SessionCleanupInterval = TimeSpan.FromMinutes(5);

    /// <summary>Interval for defensive cleanup of orphaned hub entries.</summary>
    public static readonly TimeSpan DefensiveSessionCleanupInterval = TimeSpan.FromMinutes(10);

    // ── LLM / Resilience ───────────────────────────────────────────────

    /// <summary>Timeout for quick HTTP probes (e.g. Ollama auto-detection).</summary>
    public static readonly TimeSpan HttpProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Circuit breaker sampling window.</summary>
    public static readonly TimeSpan CircuitBreakerSamplingDuration = TimeSpan.FromMinutes(1);

    /// <summary>How long a circuit stays open after tripping.</summary>
    public static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromMinutes(5);

    /// <summary>Circuit breaker failure ratio threshold.</summary>
    public const double CircuitBreakerFailureRatio = 0.5;

    /// <summary>Minimum throughput before the circuit breaker evaluates the failure ratio.</summary>
    public const int CircuitBreakerMinimumThroughput = 3;

    /// <summary>Base delay between retry attempts (used with exponential back-off).</summary>
    public static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(1);

    /// <summary>Default timeout for LLM provider API calls.</summary>
    public const int LlmDefaultTimeoutSeconds = 60;

    /// <summary>Timeout for local Ollama models (longer due to local inference).</summary>
    public const int OllamaLocalTimeoutSeconds = 300;

    /// <summary>Default max retries for new LLM providers.</summary>
    public const int LlmDefaultMaxRetries = 3;
}
