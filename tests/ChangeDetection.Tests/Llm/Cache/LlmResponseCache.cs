using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ChangeDetection.Tests.Llm.Cache;

/// <summary>
/// SQLite-based cache for LLM request/response pairs.
/// Provides cross-process thread-safe caching for deterministic testing.
/// 
/// The cache uses SHA256 hash of the request as the key to enable
/// request deduplication across test runs and processes.
/// </summary>
public sealed class LlmResponseCache : IDisposable
{
    private static readonly object SharedLock = new();
    private static LlmResponseCache? _sharedInstance;
    private static int _sharedReferenceCount;
    
    private readonly string _connectionString;
    private readonly SqliteConnection _connection;
    private readonly object _dbLock = new();
    private readonly TimeSpan _lockTimeout = TimeSpan.FromSeconds(30);
    private bool _disposed;
    private readonly bool _isSharedInstance;
    
    /// <summary>
    /// Gets or creates a shared cache instance at the default location.
    /// Thread-safe for parallel test execution.
    /// </summary>
    public static LlmResponseCache GetSharedInstance()
    {
        lock (SharedLock)
        {
            if (_sharedInstance == null || _sharedInstance._disposed)
            {
                _sharedInstance = new LlmResponseCache(GetDefaultPath(), isShared: true);
                _sharedReferenceCount = 0;
            }
            _sharedReferenceCount++;
            return _sharedInstance;
        }
    }
    
    /// <summary>
    /// Releases a reference to the shared cache. 
    /// The cache is disposed when all references are released.
    /// </summary>
    public static void ReleaseSharedInstance()
    {
        lock (SharedLock)
        {
            if (--_sharedReferenceCount <= 0 && _sharedInstance != null)
            {
                _sharedInstance._isSharedInstance.GetType(); // No-op - keep shared instance alive for test session
            }
        }
    }
    
    /// <summary>
    /// Gets the default cache database path.
    /// </summary>
    public static string GetDefaultPath() =>
        Path.Combine(
            Path.GetDirectoryName(typeof(LlmResponseCache).Assembly.Location)!,
            "..", "..", "..", "Llm", "Cache", "llm-responses.db");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Creates or opens an LLM response cache at the specified path.
    /// </summary>
    /// <param name="databasePath">Path to the SQLite database file</param>
    /// <param name="isShared">Whether this is a shared instance (affects disposal behavior)</param>
    public LlmResponseCache(string databasePath, bool isShared = false)
    {
        _isSharedInstance = isShared;
        
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = $"Data Source={databasePath};Mode=ReadWriteCreate;Cache=Shared";
        _connection = new SqliteConnection(_connectionString);
        _connection.Open();

        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        // Use WAL mode for better concurrency
        using var walCmd = _connection.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL;";
        walCmd.ExecuteNonQuery();

        // Set a busy timeout for concurrent access
        using var busyCmd = _connection.CreateCommand();
        busyCmd.CommandText = $"PRAGMA busy_timeout={(int)_lockTimeout.TotalMilliseconds};";
        busyCmd.ExecuteNonQuery();

        // Create the cache table if it doesn't exist
        using var createCmd = _connection.CreateCommand();
        createCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS llm_cache (
                request_hash TEXT PRIMARY KEY,
                request_body TEXT NOT NULL,
                response_body TEXT NOT NULL,
                response_content TEXT,
                model TEXT,
                endpoint TEXT,
                duration_ms INTEGER,
                created_at TEXT NOT NULL,
                hit_count INTEGER DEFAULT 0,
                last_hit_at TEXT
            );
            
            CREATE INDEX IF NOT EXISTS idx_model ON llm_cache(model);
            CREATE INDEX IF NOT EXISTS idx_created_at ON llm_cache(created_at);
            """;
        createCmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Computes a deterministic hash of the LLM request for cache lookup.
    /// Includes model and relevant request parameters.
    /// </summary>
    public static string ComputeRequestHash(string requestBody)
    {
        // Parse and normalize the request for consistent hashing
        string normalizedRequest;
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            var root = doc.RootElement;

            // Extract key fields that affect the response
            var model = root.TryGetProperty("model", out var m) ? m.GetString() : "";
            var messages = root.TryGetProperty("messages", out var msgs) ? msgs.ToString() : "";
            var temperature = root.TryGetProperty("temperature", out var t) ? t.GetDouble() : 0.7;

            normalizedRequest = $"{model}|{temperature:F2}|{messages}";
        }
        catch
        {
            // Fall back to raw body if parsing fails
            normalizedRequest = requestBody;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRequest));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Tries to get a cached response for the given request.
    /// </summary>
    /// <returns>The cached response body, or null if not found</returns>
    public CachedLlmResponse? TryGet(string requestBody)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var hash = ComputeRequestHash(requestBody);

        lock (_dbLock)
        {
            using var selectCmd = _connection.CreateCommand();
            selectCmd.CommandText = """
                SELECT response_body, response_content, model, duration_ms
                FROM llm_cache
                WHERE request_hash = @hash
                """;
            selectCmd.Parameters.AddWithValue("@hash", hash);

            using var reader = selectCmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var result = new CachedLlmResponse
            {
                ResponseBody = reader.GetString(0),
                ResponseContent = reader.IsDBNull(1) ? null : reader.GetString(1),
                Model = reader.IsDBNull(2) ? null : reader.GetString(2),
                DurationMs = reader.IsDBNull(3) ? 0 : reader.GetInt64(3)
            };
            
            // Close reader before updating
            reader.Close();

            // Update hit statistics
            using var updateCmd = _connection.CreateCommand();
            updateCmd.CommandText = """
                UPDATE llm_cache
                SET hit_count = hit_count + 1, last_hit_at = @now
                WHERE request_hash = @hash
                """;
            updateCmd.Parameters.AddWithValue("@hash", hash);
            updateCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            updateCmd.ExecuteNonQuery();

            return result;
        }
    }

    /// <summary>
    /// Stores a request/response pair in the cache.
    /// Uses INSERT OR REPLACE for idempotency.
    /// </summary>
    public void Store(string requestBody, string responseBody, string? model = null, string? endpoint = null, long durationMs = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var hash = ComputeRequestHash(requestBody);
        var content = ExtractResponseContent(responseBody);

        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO llm_cache 
                (request_hash, request_body, response_body, response_content, model, endpoint, duration_ms, created_at)
                VALUES (@hash, @request, @response, @content, @model, @endpoint, @duration, @created)
                """;
            cmd.Parameters.AddWithValue("@hash", hash);
            cmd.Parameters.AddWithValue("@request", requestBody);
            cmd.Parameters.AddWithValue("@response", responseBody);
            cmd.Parameters.AddWithValue("@content", content ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@model", model ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@endpoint", endpoint ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@duration", durationMs);
            cmd.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("O"));

            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Extracts the assistant's response content from an OpenAI-format response.
    /// </summary>
    private static string? ExtractResponseContent(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) &&
                choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var content))
                {
                    return content.GetString();
                }
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return null;
    }

    /// <summary>
    /// Gets statistics about the cache.
    /// </summary>
    public CacheStatistics GetStatistics()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT 
                    COUNT(*) as total_entries,
                    SUM(hit_count) as total_hits,
                    COUNT(CASE WHEN hit_count > 0 THEN 1 END) as entries_with_hits,
                    MIN(created_at) as oldest_entry,
                    MAX(created_at) as newest_entry
                FROM llm_cache
                """;

            using var reader = cmd.ExecuteReader();
            reader.Read();

            return new CacheStatistics
            {
                TotalEntries = reader.GetInt64(0),
                TotalHits = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                EntriesWithHits = reader.GetInt64(2),
                OldestEntry = reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3)),
                NewestEntry = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4))
            };
        }
    }

    /// <summary>
    /// Lists all cached entries with their hashes and models.
    /// </summary>
    public List<CacheEntry> ListEntries()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_dbLock)
        {
            var entries = new List<CacheEntry>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT request_hash, model, hit_count, created_at, response_content
                FROM llm_cache
                ORDER BY created_at DESC
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new CacheEntry
                {
                    RequestHash = reader.GetString(0),
                    Model = reader.IsDBNull(1) ? null : reader.GetString(1),
                    HitCount = reader.GetInt64(2),
                    CreatedAt = DateTime.Parse(reader.GetString(3)),
                    ResponsePreview = reader.IsDBNull(4) ? null : TruncateString(reader.GetString(4), 100)
                });
            }
            return entries;
        }
    }

    /// <summary>
    /// Clears all entries from the cache.
    /// </summary>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM llm_cache";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Removes entries older than the specified age.
    /// </summary>
    public int PruneOlderThan(TimeSpan age)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cutoff = DateTime.UtcNow - age;

        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM llm_cache WHERE created_at < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));

            return cmd.ExecuteNonQuery();
        }
    }

    private static string TruncateString(string value, int maxLength)
    {
        return value.Length <= maxLength 
            ? value 
            : string.Concat(value.AsSpan(0, maxLength - 3), "...");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Close();
        _connection.Dispose();
    }
}

/// <summary>
/// A cached LLM response retrieved from the cache.
/// </summary>
public record CachedLlmResponse
{
    /// <summary>The full HTTP response body.</summary>
    public required string ResponseBody { get; init; }
    
    /// <summary>The extracted assistant message content.</summary>
    public string? ResponseContent { get; init; }
    
    /// <summary>The model that generated this response.</summary>
    public string? Model { get; init; }
    
    /// <summary>Original response time in milliseconds.</summary>
    public long DurationMs { get; init; }
}

/// <summary>
/// Statistics about the LLM response cache.
/// </summary>
public record CacheStatistics
{
    public long TotalEntries { get; init; }
    public long TotalHits { get; init; }
    public long EntriesWithHits { get; init; }
    public DateTime? OldestEntry { get; init; }
    public DateTime? NewestEntry { get; init; }
}

/// <summary>
/// Summary of a cache entry for listing.
/// </summary>
public record CacheEntry
{
    public required string RequestHash { get; init; }
    public string? Model { get; init; }
    public long HitCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? ResponsePreview { get; init; }
}
