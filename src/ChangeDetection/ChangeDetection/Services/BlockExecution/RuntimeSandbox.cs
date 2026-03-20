using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.BlockExecution;

public class ResourceExhaustedException(string resource, string detail)
    : Exception($"Resource exhausted: {resource}. {detail}");

public class SecurityViolationException(string detail)
    : Exception($"Security violation: {detail}");

public class ExecutionBudget
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private int _httpRequestCount;
    private int _playwrightNavigationCount;
    private long _totalBytesReceived;

    public int MaxHttpRequests { get; init; } = 200;
    public int MaxPlaywrightNavigations { get; init; } = 10;
    public long MaxResponseSizeBytes { get; init; } = 5 * 1024 * 1024;
    public int MaxExecutionTimeSeconds { get; init; } = 120;
    public int MaxStateKeys { get; init; } = 100;
    public long MaxStateSizeBytes { get; init; } = 1024 * 1024;

    public int HttpRequestCount => Volatile.Read(ref _httpRequestCount);
    public int PlaywrightNavigationCount => Volatile.Read(ref _playwrightNavigationCount);
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);

    public void RecordHttpRequest()
    {
        CheckTimeout();

        var count = Interlocked.Increment(ref _httpRequestCount);
        if (count > MaxHttpRequests)
        {
            Interlocked.Decrement(ref _httpRequestCount);
            throw new ResourceExhaustedException(
                "http_requests",
                $"Exceeded {MaxHttpRequests} requests in a single pipeline run.");
        }
    }

    public void RecordNavigation()
    {
        CheckTimeout();

        var count = Interlocked.Increment(ref _playwrightNavigationCount);
        if (count > MaxPlaywrightNavigations)
        {
            Interlocked.Decrement(ref _playwrightNavigationCount);
            throw new ResourceExhaustedException(
                "playwright_navigations",
                $"Exceeded {MaxPlaywrightNavigations} Playwright navigations in a single pipeline run.");
        }
    }

    public void RecordBytesReceived(long bytes)
    {
        if (bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(bytes), "Received byte count cannot be negative.");

        CheckTimeout();

        var total = Interlocked.Add(ref _totalBytesReceived, bytes);
        if (total > MaxResponseSizeBytes)
        {
            Interlocked.Add(ref _totalBytesReceived, -bytes);
            throw new ResourceExhaustedException(
                "response_bytes",
                $"Received {total:N0} bytes, exceeding the {MaxResponseSizeBytes:N0} byte runtime limit.");
        }
    }

    public void CheckTimeout()
    {
        if (_stopwatch.Elapsed > TimeSpan.FromSeconds(MaxExecutionTimeSeconds))
        {
            throw new ResourceExhaustedException(
                "execution_time",
                $"Execution exceeded {MaxExecutionTimeSeconds} seconds.");
        }
    }

    internal TimeSpan GetRemainingTime()
    {
        CheckTimeout();
        return TimeSpan.FromSeconds(MaxExecutionTimeSeconds) - _stopwatch.Elapsed;
    }
}

public class NamespacedStateStore(IBlockStateStore inner, string pipelineId, ExecutionBudget budget) : IBlockStateStore
{
    private readonly string _namespacePrefix = CreateNamespacePrefix(pipelineId);
    private readonly ConcurrentDictionary<string, long> _trackedStateKeys = new(StringComparer.Ordinal);
    private readonly object _trackingLock = new();
    private long _trackedStateSizeBytes;

    public Task<JsonElement?> GetPreviousOutputAsync(string watchId, string blockInstanceId, CancellationToken ct = default)
    {
        ValidateKeyPart(watchId, nameof(watchId));
        ValidateKeyPart(blockInstanceId, nameof(blockInstanceId));
        return inner.GetPreviousOutputAsync(GetNamespacedWatchId(watchId), blockInstanceId, ct);
    }

    public Task<JsonElement?> GetCachedOutputAsync(
        string watchId,
        string blockInstanceId,
        string inputHash,
        string pipelineHash,
        CancellationToken ct = default)
    {
        ValidateKeyPart(watchId, nameof(watchId));
        ValidateKeyPart(blockInstanceId, nameof(blockInstanceId));
        return inner.GetCachedOutputAsync(
            GetNamespacedWatchId(watchId),
            blockInstanceId,
            inputHash,
            pipelineHash,
            ct);
    }

    public Task SaveOutputAsync(
        string watchId,
        string blockInstanceId,
        JsonElement output,
        string? inputHash = null,
        string? pipelineHash = null,
        CancellationToken ct = default)
    {
        ValidateKeyPart(watchId, nameof(watchId));
        ValidateKeyPart(blockInstanceId, nameof(blockInstanceId));
        TrackStateWrite(watchId, blockInstanceId, inputHash, pipelineHash, output);

        return inner.SaveOutputAsync(
            GetNamespacedWatchId(watchId),
            blockInstanceId,
            output,
            inputHash,
            pipelineHash,
            ct);
    }

    public Task<IReadOnlyList<BlockExecutionSnapshot>> GetHistoryAsync(
        string watchId,
        string blockInstanceId,
        int maxResults = 10,
        CancellationToken ct = default)
    {
        ValidateKeyPart(watchId, nameof(watchId));
        ValidateKeyPart(blockInstanceId, nameof(blockInstanceId));
        return inner.GetHistoryAsync(GetNamespacedWatchId(watchId), blockInstanceId, maxResults, ct);
    }

    private void TrackStateWrite(
        string watchId,
        string blockInstanceId,
        string? inputHash,
        string? pipelineHash,
        JsonElement output)
    {
        var stateKey = $"{_namespacePrefix}{watchId}:{blockInstanceId}:{inputHash ?? string.Empty}:{pipelineHash ?? string.Empty}";
        var stateSize = Encoding.UTF8.GetByteCount(output.GetRawText());

        if (stateSize > budget.MaxStateSizeBytes)
        {
            throw new ResourceExhaustedException(
                "state_size",
                $"Single state payload ({stateSize:N0} bytes) exceeds the {budget.MaxStateSizeBytes:N0} byte limit.");
        }

        lock (_trackingLock)
        {
            var existingSize = _trackedStateKeys.TryGetValue(stateKey, out var previousSize)
                ? previousSize
                : 0L;

            var projectedKeyCount = existingSize == 0
                ? _trackedStateKeys.Count + 1
                : _trackedStateKeys.Count;

            if (projectedKeyCount > budget.MaxStateKeys)
            {
                throw new ResourceExhaustedException(
                    "state_keys",
                    $"Tracked {projectedKeyCount} state keys, exceeding the {budget.MaxStateKeys} key limit.");
            }

            var projectedSize = _trackedStateSizeBytes - existingSize + stateSize;
            if (projectedSize > budget.MaxStateSizeBytes)
            {
                throw new ResourceExhaustedException(
                    "state_size",
                    $"Tracked state would grow to {projectedSize:N0} bytes, exceeding the {budget.MaxStateSizeBytes:N0} byte limit.");
            }

            _trackedStateKeys[stateKey] = stateSize;
            _trackedStateSizeBytes = projectedSize;
        }
    }

    private string GetNamespacedWatchId(string watchId) => $"{_namespacePrefix}{watchId}";

    private static string CreateNamespacePrefix(string pipelineId)
    {
        if (string.IsNullOrWhiteSpace(pipelineId))
            throw new ArgumentException("Pipeline id must not be empty.", nameof(pipelineId));

        return $"pipeline:{pipelineId.Trim()}:";
    }

    private static void ValidateKeyPart(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("State key parts must not be empty.", paramName);

        if (value.StartsWith("pipeline:", StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityViolationException(
                $"State access attempted to inject a reserved namespace via '{paramName}'.");
        }
    }
}

public class PinnedHttpClient(
    IHttpClientFactory httpClientFactory,
    DomainPinValidator domainPinValidator,
    ILogger<PinnedHttpClient> logger)
{
    private const int MaxRedirects = 5;
    private const string RuntimeSandboxClientName = "RuntimeSandbox";

    public async Task<HttpResponseMessage> SendAsync(
        string url,
        DomainPin pin,
        ExecutionBudget budget,
        HttpMethod? method = null,
        HttpContent? body = null,
        Dictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(budget);

        var requestMethod = method ?? HttpMethod.Get;
        var bodyBytes = body is null ? null : await body.ReadAsByteArrayAsync(ct);
        var bodyHeaders = body?.Headers
            .Select(header => new HeaderValues(header.Key, header.Value.ToArray()))
            .ToArray();

        var currentUrl = url;
        var currentMethod = requestMethod;
        var currentBodyBytes = bodyBytes;

        for (var redirectCount = 0; ; redirectCount++)
        {
            budget.CheckTimeout();
            await EnsureAllowedAsync(currentUrl, pin, ct);
            budget.RecordHttpRequest();

            var client = httpClientFactory.CreateClient(RuntimeSandboxClientName);
            client.Timeout = Timeout.InfiniteTimeSpan;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(budget.GetRemainingTime());

            var request = BuildRequest(currentUrl, currentMethod, currentBodyBytes, bodyHeaders, headers);

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Runtime sandbox HTTP request timed out for {Url}", currentUrl);
                throw new ResourceExhaustedException(
                    "execution_time",
                    $"HTTP request to '{currentUrl}' exceeded the remaining execution budget.");
            }

            await BufferResponseContentAsync(response, budget, timeoutCts.Token);

            if (!IsRedirectStatus(response.StatusCode))
                return response;

            if (redirectCount >= MaxRedirects)
            {
                response.Dispose();
                throw new SecurityViolationException(
                    $"Redirect limit exceeded while requesting '{url}'.");
            }

            if (response.Headers.Location is null)
            {
                response.Dispose();
                throw new SecurityViolationException(
                    $"Redirect response from '{currentUrl}' did not include a Location header.");
            }

            var redirectTarget = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(new Uri(currentUrl, UriKind.Absolute), response.Headers.Location);

            await EnsureAllowedAsync(redirectTarget.ToString(), pin, ct);

            logger.LogInformation(
                "Runtime sandbox following redirect {RedirectNumber} from {Source} to {Target}",
                redirectCount + 1,
                currentUrl,
                redirectTarget);

            if (ShouldConvertRedirectToGet(response.StatusCode, currentMethod))
            {
                currentMethod = HttpMethod.Get;
                currentBodyBytes = null;
            }

            currentUrl = redirectTarget.ToString();
            response.Dispose();
        }
    }

    private async Task EnsureAllowedAsync(string url, DomainPin pin, CancellationToken ct)
    {
        var validationError = await domainPinValidator.ValidateWithDnsResolution(url, pin, ct);
        if (validationError is not null)
        {
            logger.LogWarning("Runtime sandbox blocked {Url}: {Reason}", url, validationError);
            throw new SecurityViolationException(validationError);
        }
    }

    private static HttpRequestMessage BuildRequest(
        string url,
        HttpMethod method,
        byte[]? bodyBytes,
        IReadOnlyList<HeaderValues>? bodyHeaders,
        Dictionary<string, string>? headers)
    {
        var request = new HttpRequestMessage(method, url);

        if (bodyBytes is not null)
        {
            var content = new ByteArrayContent(bodyBytes);
            if (bodyHeaders is not null)
            {
                foreach (var header in bodyHeaders)
                    content.Headers.TryAddWithoutValidation(header.Name, header.Values);
            }

            request.Content = content;
        }

        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));

        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                if (string.Equals(name, "Accept-Encoding", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!request.Headers.TryAddWithoutValidation(name, value))
                    request.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return request;
    }

    private static async Task BufferResponseContentAsync(
        HttpResponseMessage response,
        ExecutionBudget budget,
        CancellationToken ct)
    {
        if (response.Content is null)
            return;

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > budget.MaxResponseSizeBytes)
        {
            throw new ResourceExhaustedException(
                "response_size",
                $"Response from '{response.RequestMessage?.RequestUri}' declared {contentLength.Value:N0} bytes, exceeding the {budget.MaxResponseSizeBytes:N0} byte limit.");
        }

        using var originalStream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long totalRead = 0;

        while (true)
        {
            var read = await originalStream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct);
            if (read == 0)
                break;

            totalRead += read;
            if (totalRead > budget.MaxResponseSizeBytes)
            {
                throw new ResourceExhaustedException(
                    "response_size",
                    $"Response from '{response.RequestMessage?.RequestUri}' exceeded the {budget.MaxResponseSizeBytes:N0} byte limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        budget.RecordBytesReceived(totalRead);

        var replacementContent = new ByteArrayContent(buffer.ToArray());
        foreach (var header in response.Content.Headers)
        {
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;

            replacementContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content.Dispose();
        response.Content = replacementContent;
    }

    private static bool IsRedirectStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool ShouldConvertRedirectToGet(HttpStatusCode statusCode, HttpMethod method) =>
        statusCode == HttpStatusCode.RedirectMethod
        || ((statusCode == HttpStatusCode.MovedPermanently || statusCode == HttpStatusCode.Found)
            && method != HttpMethod.Get
            && method != HttpMethod.Head);

    private sealed record HeaderValues(string Name, IReadOnlyList<string> Values);
}
