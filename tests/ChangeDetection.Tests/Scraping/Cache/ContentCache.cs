using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ChangeDetection.Tests.Scraping.Cache;

/// <summary>
/// SQLite-based cache for web content fetch results.
/// Provides cross-process thread-safe caching for deterministic testing.
/// 
/// The cache uses SHA256 hash of the URL as the key to enable
/// content deduplication across test runs and processes.
/// </summary>
public sealed class ContentCache : IDisposable
{
    private static readonly object SharedLock = new();
    private static ContentCache? _sharedInstance;
    private static int _sharedReferenceCount;
    
    private readonly SqliteConnection _connection;
    private readonly object _dbLock = new();
    private readonly TimeSpan _lockTimeout = TimeSpan.FromSeconds(30);
    private bool _disposed;
    private readonly bool _isSharedInstance;
    
    /// <summary>
    /// Gets or creates a shared cache instance at the default location.
    /// Thread-safe for parallel test execution.
    /// </summary>
    public static ContentCache GetSharedInstance()
    {
        lock (SharedLock)
        {
            if (_sharedInstance == null || _sharedInstance._disposed)
            {
                _sharedInstance = new ContentCache(GetDefaultPath(), isShared: true);
                _sharedReferenceCount = 0;
            }
            _sharedReferenceCount++;
            return _sharedInstance;
        }
    }
    
    /// <summary>
    /// Gets the default cache database path.
    /// </summary>
    public static string GetDefaultPath() =>
        Path.Combine(
            Path.GetDirectoryName(typeof(ContentCache).Assembly.Location)!,
            "..", "..", "..", "Scraping", "Cache", "content-cache.db");

    /// <summary>
    /// Creates or opens a content cache at the specified path.
    /// </summary>
    /// <param name="databasePath">Path to the SQLite database file</param>
    /// <param name="isShared">Whether this is a shared instance (affects disposal behavior)</param>
    public ContentCache(string databasePath, bool isShared = false)
    {
        _isSharedInstance = isShared;
        
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = $"Data Source={databasePath};Mode=ReadWriteCreate;Cache=Shared";
        _connection = new SqliteConnection(connectionString);
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
            CREATE TABLE IF NOT EXISTS content_cache (
                url_hash TEXT PRIMARY KEY,
                url TEXT NOT NULL,
                html TEXT,
                http_status_code INTEGER,
                error_message TEXT,
                response_headers TEXT,
                duration_ms INTEGER,
                is_success INTEGER,
                created_at TEXT NOT NULL,
                hit_count INTEGER DEFAULT 0,
                last_hit_at TEXT
            );
            
            CREATE INDEX IF NOT EXISTS idx_url ON content_cache(url);
            CREATE INDEX IF NOT EXISTS idx_created_at ON content_cache(created_at);
            """;
        createCmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Computes a deterministic hash of the URL for cache lookup.
    /// </summary>
    public static string ComputeUrlHash(string url)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim().ToLowerInvariant()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Tries to get a cached fetch result for the given URL.
    /// </summary>
    public CachedContentEntry? TryGet(string url)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var hash = ComputeUrlHash(url);

        lock (_dbLock)
        {
            using var selectCmd = _connection.CreateCommand();
            selectCmd.CommandText = """
                SELECT url, html, http_status_code, error_message, response_headers, duration_ms, is_success
                FROM content_cache
                WHERE url_hash = @hash
                """;
            selectCmd.Parameters.AddWithValue("@hash", hash);

            using var reader = selectCmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var headersJson = reader.IsDBNull(4) ? null : reader.GetString(4);
            var headers = headersJson != null 
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson) ?? []
                : [];

            var result = new CachedContentEntry
            {
                Url = reader.GetString(0),
                Html = reader.IsDBNull(1) ? null : reader.GetString(1),
                HttpStatusCode = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                ErrorMessage = reader.IsDBNull(3) ? null : reader.GetString(3),
                ResponseHeaders = headers,
                DurationMs = reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                IsSuccess = reader.GetInt32(6) == 1
            };
            
            reader.Close();

            // Update hit statistics
            using var updateCmd = _connection.CreateCommand();
            updateCmd.CommandText = """
                UPDATE content_cache
                SET hit_count = hit_count + 1,
                    last_hit_at = @now
                WHERE url_hash = @hash
                """;
            updateCmd.Parameters.AddWithValue("@hash", hash);
            updateCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            updateCmd.ExecuteNonQuery();

            return result;
        }
    }

    /// <summary>
    /// Stores a fetch result in the cache.
    /// </summary>
    public void Store(string url, CachedContentEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var hash = ComputeUrlHash(url);
        var headersJson = JsonSerializer.Serialize(entry.ResponseHeaders);

        lock (_dbLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO content_cache 
                (url_hash, url, html, http_status_code, error_message, response_headers, duration_ms, is_success, created_at)
                VALUES (@hash, @url, @html, @statusCode, @errorMessage, @headers, @durationMs, @isSuccess, @createdAt)
                """;
            cmd.Parameters.AddWithValue("@hash", hash);
            cmd.Parameters.AddWithValue("@url", url);
            cmd.Parameters.AddWithValue("@html", entry.Html ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@statusCode", entry.HttpStatusCode);
            cmd.Parameters.AddWithValue("@errorMessage", entry.ErrorMessage ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@headers", headersJson);
            cmd.Parameters.AddWithValue("@durationMs", entry.DurationMs);
            cmd.Parameters.AddWithValue("@isSuccess", entry.IsSuccess ? 1 : 0);
            cmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Gets the total number of cached entries.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_dbLock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM content_cache";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        if (!_isSharedInstance)
        {
            _connection.Dispose();
        }
    }
}

/// <summary>
/// A cached content entry.
/// </summary>
public class CachedContentEntry
{
    public required string Url { get; init; }
    public string? Html { get; init; }
    public int HttpStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public Dictionary<string, string> ResponseHeaders { get; init; } = [];
    public long DurationMs { get; init; }
    public bool IsSuccess { get; init; }
}
