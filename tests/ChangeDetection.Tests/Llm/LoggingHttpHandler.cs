using System.Text;
using System.Text.Json;

namespace ChangeDetection.Tests.Llm;

/// <summary>
/// HTTP handler that logs all requests and responses to the test output.
/// Use this to capture real Ollama traffic for creating mock fixtures.
/// 
/// Usage:
/// 1. Create handler with TestContext.Current?.OutputWriter as the output
/// 2. Wrap your normal HttpClient handler
/// 3. Run tests - all HTTP traffic will be logged
/// 4. Copy the logged JSON responses to use as mock fixtures
/// </summary>
public class LoggingHttpHandler : DelegatingHandler
{
    private readonly TextWriter _output;
    private readonly bool _logRequestBody;
    private readonly bool _logResponseBody;
    private readonly List<CapturedExchange> _captured = [];

    public LoggingHttpHandler(
        TextWriter output,
        HttpMessageHandler? innerHandler = null,
        bool logRequestBody = true,
        bool logResponseBody = true)
    {
        _output = output;
        _logRequestBody = logRequestBody;
        _logResponseBody = logResponseBody;
        InnerHandler = innerHandler ?? new HttpClientHandler();
    }

    /// <summary>
    /// All captured HTTP exchanges for later analysis.
    /// </summary>
    public IReadOnlyList<CapturedExchange> CapturedExchanges => _captured;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var requestTimestamp = DateTime.UtcNow;
        string? requestBody = null;

        // Log request
        _output.WriteLine("═══════════════════════════════════════════════════════════════");
        _output.WriteLine($"REQUEST: {request.Method} {request.RequestUri}");
        _output.WriteLine($"Timestamp: {requestTimestamp:O}");
        _output.WriteLine("───────────────────────────────────────────────────────────────");

        if (_logRequestBody && request.Content != null)
        {
            requestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            // Try to pretty-print JSON
            try
            {
                var json = JsonDocument.Parse(requestBody);
                var pretty = JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true });
                _output.WriteLine("Request Body (JSON):");
                _output.WriteLine(pretty);
            }
            catch
            {
                _output.WriteLine("Request Body:");
                _output.WriteLine(requestBody);
            }
        }

        _output.WriteLine("───────────────────────────────────────────────────────────────");

        // Execute request
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var response = await base.SendAsync(request, cancellationToken);
        stopwatch.Stop();

        // Log response
        _output.WriteLine($"RESPONSE: {(int)response.StatusCode} {response.StatusCode}");
        _output.WriteLine($"Duration: {stopwatch.ElapsedMilliseconds}ms");
        _output.WriteLine("───────────────────────────────────────────────────────────────");

        string? responseBody = null;
        if (_logResponseBody)
        {
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            // Try to pretty-print JSON
            try
            {
                var json = JsonDocument.Parse(responseBody);
                var pretty = JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true });
                _output.WriteLine("Response Body (JSON):");
                _output.WriteLine(pretty);
            }
            catch
            {
                _output.WriteLine("Response Body:");
                _output.WriteLine(responseBody);
            }

            // Recreate content since we consumed it
            response.Content = new StringContent(responseBody, Encoding.UTF8, response.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        _output.WriteLine("═══════════════════════════════════════════════════════════════");
        _output.WriteLine();

        var exchange = new CapturedExchange
        {
            RequestMethod = request.Method.Method,
            RequestUri = request.RequestUri?.ToString() ?? "",
            RequestBody = requestBody,
            RequestTimestamp = requestTimestamp,
            ResponseStatusCode = (int)response.StatusCode,
            ResponseBody = responseBody,
            DurationMs = stopwatch.ElapsedMilliseconds
        };

        _captured.Add(exchange);
        return response;
    }
}

/// <summary>
/// Represents a captured HTTP request/response exchange.
/// </summary>
public record CapturedExchange
{
    public string RequestMethod { get; init; } = "";
    public string RequestUri { get; init; } = "";
    public string? RequestBody { get; init; }
    public DateTime RequestTimestamp { get; init; }
    public int ResponseStatusCode { get; init; }
    public string? ResponseBody { get; init; }
    public long DurationMs { get; init; }

    /// <summary>
    /// Extracts the messages array from the OpenAI-format request.
    /// </summary>
    public string? GetPromptFromRequest()
    {
        if (string.IsNullOrEmpty(RequestBody)) return null;

        try
        {
            using var doc = JsonDocument.Parse(RequestBody);
            if (doc.RootElement.TryGetProperty("messages", out var messages))
            {
                var result = new StringBuilder();
                foreach (var msg in messages.EnumerateArray())
                {
                    var role = msg.GetProperty("role").GetString();
                    var content = msg.GetProperty("content").GetString();
                    result.AppendLine($"[{role}]: {content}");
                }
                return result.ToString();
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Extracts the assistant message content from the OpenAI-format response.
    /// </summary>
    public string? GetResponseContent()
    {
        if (string.IsNullOrEmpty(ResponseBody)) return null;

        try
        {
            using var doc = JsonDocument.Parse(ResponseBody);
            if (doc.RootElement.TryGetProperty("choices", out var choices))
            {
                var first = choices.EnumerateArray().FirstOrDefault();
                if (first.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var content))
                {
                    return content.GetString();
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Formats this exchange as a copyable mock fixture.
    /// </summary>
    public string ToMockFixture()
    {
        var content = GetResponseContent();
        if (content == null) return "// Could not extract response content";

        // Escape for C# string literal
        var escaped = content
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "")
            .Replace("\n", "\\n");

        return $"\"{escaped}\"";
    }
}
