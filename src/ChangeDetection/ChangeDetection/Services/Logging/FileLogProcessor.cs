using System.Text;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace ChangeDetection.Services.Logging;

/// <summary>
/// OpenTelemetry log processor that writes logs to a file with daily rolling.
/// </summary>
public sealed class FileLogProcessor : BaseProcessor<LogRecord>, IDisposable
{
    private readonly string _logsDirectory;
    private readonly object _lock = new();
    private readonly int _retainedFileCountLimit;
    private StreamWriter? _writer;
    private string _currentDate = "";

    public FileLogProcessor(string logFilePath, int retainedFileCountLimit = 7)
    {
        _logsDirectory = Path.GetDirectoryName(logFilePath) ?? ".";
        _retainedFileCountLimit = retainedFileCountLimit;
        Directory.CreateDirectory(_logsDirectory);
        EnsureWriter();
        CleanupOldFiles();
    }

    public override void OnEnd(LogRecord logRecord)
    {
        lock (_lock)
        {
            EnsureWriter();
            
            var timestamp = logRecord.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
            var level = logRecord.LogLevel.ToString().ToUpperInvariant()[..3];
            var category = logRecord.CategoryName ?? "Unknown";
            var message = logRecord.FormattedMessage ?? logRecord.Body?.ToString() ?? "";
            
            var sb = new StringBuilder();
            sb.Append(timestamp);
            sb.Append(" [");
            sb.Append(level);
            sb.Append("] ");
            sb.Append(category);
            sb.Append(": ");
            sb.AppendLine(message);
            
            if (logRecord.Exception is not null)
            {
                sb.AppendLine(logRecord.Exception.ToString());
            }
            
            _writer?.Write(sb.ToString());
            _writer?.Flush();
        }
    }

    private void EnsureWriter()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (today == _currentDate && _writer is not null)
        {
            return;
        }

        _writer?.Dispose();
        _currentDate = today;
        var filePath = Path.Combine(_logsDirectory, $"log-{today}.txt");
        
        // Use FileShare.ReadWrite to allow multiple processes (including test instances) to write to the same log file
        var fileStream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(fileStream, Encoding.UTF8)
        {
            AutoFlush = false
        };
    }

    private void CleanupOldFiles()
    {
        try
        {
            var logFiles = Directory.GetFiles(_logsDirectory, "log-*.txt")
                .OrderByDescending(f => f)
                .Skip(_retainedFileCountLimit)
                .ToList();

            foreach (var file in logFiles)
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lock)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
        
        base.Dispose(disposing);
    }
}
