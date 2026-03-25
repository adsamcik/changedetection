using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.BlockExecution;

#region Records & Enums

public enum DegradationAction { Abort, Skip, Fallback, Retry }

public record DegradationDecision(
    DegradationAction Action,
    string Reason,
    string? FallbackBlockType = null,
    int? RetryDelayMs = null);

public record JsonStructureFingerprint(
    IReadOnlyList<string> TopLevelKeys,
    IReadOnlyDictionary<string, string> KeyTypes,
    int MaxDepth,
    int EstimatedNodeCount);

public record AutoHealSuggestion(
    string OriginalPath,
    string SuggestedPath,
    string Reason,
    double Confidence);

#endregion

public class PipelineDegradationService(ILogger<PipelineDegradationService> logger)
{
    private const int MaxAutoHealAttempts = 3;
    private const int MaxFingerprintDepth = 3;

    private static readonly HashSet<string> AcquisitionBlocks =
        ["Navigate", "HttpRequest", "Paginate", "ForEachRequest", "Iterate"];

    private static readonly HashSet<string> EnhancementBlocks =
        ["LlmExtract", "LlmEvaluate", "LlmCraftPrompt", "RelevanceScore", "Enrich", "LinkValidate"];

    // ───────────────────────── Degradation ─────────────────────────

    public DegradationDecision DecideOnFailure(
        string blockType, BlockCriticalityTier tier, string errorMessage, int attemptNumber)
    {
        if (EnhancementBlocks.Contains(blockType))
        {
            logger.LogWarning("{Block} failed — skipping. Running in dumb scraper mode", blockType);
            return new(DegradationAction.Skip, $"{blockType} unavailable, continuing without enhancement");
        }

        if (AcquisitionBlocks.Contains(blockType))
            return DecideAcquisition(blockType, errorMessage, attemptNumber);

        return tier switch
        {
            BlockCriticalityTier.Infrastructure =>
                new(DegradationAction.Abort, $"Infrastructure block {blockType} failed: {errorMessage}"),

            BlockCriticalityTier.Acquisition =>
                new(DegradationAction.Abort, $"Acquisition block {blockType} failed: {errorMessage}"),

            BlockCriticalityTier.Extraction => DecideExtraction(blockType, errorMessage, attemptNumber),

            BlockCriticalityTier.Analysis =>
                new(DegradationAction.Abort, $"Analysis block {blockType} failed — results are critical"),

            BlockCriticalityTier.Delivery =>
                new(DegradationAction.Skip, $"Delivery block {blockType} failed — will retry via outbox"),

            _ => new(DegradationAction.Abort, $"Unknown tier for {blockType}: {errorMessage}")
        };
    }

    private DegradationDecision DecideAcquisition(string blockType, string error, int attempt)
    {
        var errorLower = error.ToLowerInvariant();

        if (blockType == "HttpRequest" && (errorLower.Contains("403") || errorLower.Contains("gone")))
            return new(DegradationAction.Fallback, "HTTP 403/gone — falling back to browser navigation",
                FallbackBlockType: "Navigate");

        return attempt switch
        {
            1 => new(DegradationAction.Retry, $"{blockType} attempt 1 failed — retrying", RetryDelayMs: 1000),
            2 => new(DegradationAction.Retry, $"{blockType} attempt 2 failed — retrying", RetryDelayMs: 3000),
            3 when errorLower.Contains("timeout") || errorLower.Contains("rate limit") =>
                new(DegradationAction.Retry, $"{blockType} timeout/rate-limit — final retry", RetryDelayMs: 10000),
            _ => new(DegradationAction.Abort, $"{blockType} failed after {attempt} attempts: {error}")
        };
    }

    private DegradationDecision DecideExtraction(string blockType, string error, int attempt)
    {
        return attempt switch
        {
            1 => new(DegradationAction.Retry, $"{blockType} attempt 1 failed — retrying", RetryDelayMs: 500),
            _ => new(DegradationAction.Abort,
                $"{blockType} extraction failed after {attempt} attempts — bad data is worse than no data")
        };
    }

    // ──────────────────────── JSON Auto-Heal ────────────────────────

    public async Task<IReadOnlyList<AutoHealSuggestion>> DiagnoseJsonPathFailureAsync(
        Guid watchId,
        string failedPath,
        string currentJsonSample,
        IBlockStateStore stateStore,
        CancellationToken ct = default)
    {
        var attempts = await GetAutoHealAttemptsAsync(watchId, stateStore, ct);
        if (attempts >= MaxAutoHealAttempts)
        {
            logger.LogWarning("Watch {WatchId} exceeded max auto-heal attempts ({Max})", watchId, MaxAutoHealAttempts);
            return [];
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(currentJsonSample); }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Cannot parse JSON sample for auto-heal on watch {WatchId}", watchId);
            return [];
        }

        using (doc)
        {
            var current = BuildFingerprint(doc.RootElement);
            var previous = await LoadFingerprintAsync(watchId, stateStore, ct);
            if (previous is null) return [];

            var targetKey = ExtractTargetKey(failedPath);
            if (targetKey is null) return [];

            var suggestions = new List<AutoHealSuggestion>();
            var wasTopLevel = previous.TopLevelKeys.Contains(targetKey);
            var isTopLevel = current.TopLevelKeys.Contains(targetKey);

            // Key moved deeper
            if (wasTopLevel && !isTopLevel)
            {
                var found = FindKeyPath(doc.RootElement, targetKey, "$", MaxFingerprintDepth);
                if (found is not null)
                {
                    var pathIdx = failedPath.IndexOf(targetKey, StringComparison.Ordinal);
                    var suffix = pathIdx >= 0 ? failedPath[(pathIdx + targetKey.Length)..] : "";
                    var wrapper = found.Replace("$.", "").Replace($".{targetKey}", "");
                    suggestions.Add(new(failedPath, $"{found}{suffix}",
                        $"Key '{targetKey}' moved under '{wrapper}'", 0.85));
                }

                // Also check for rename (same type, new key)
                var prevType = previous.KeyTypes.GetValueOrDefault(targetKey);
                foreach (var key in current.TopLevelKeys.Except(previous.TopLevelKeys))
                {
                    if (current.KeyTypes.GetValueOrDefault(key) == prevType)
                        suggestions.Add(new(failedPath, failedPath.Replace(targetKey, key),
                            $"Key '{targetKey}' possibly renamed to '{key}' (same type: {prevType})", 0.6));
                }
            }

            // Type changed (array ↔ object)
            if (previous.KeyTypes.TryGetValue(targetKey, out var pType)
                && current.KeyTypes.TryGetValue(targetKey, out var cType)
                && pType != cType)
            {
                if (pType == "array" && cType == "object")
                    suggestions.Add(new(failedPath, failedPath.Replace("[*]", ""),
                        $"'{targetKey}' changed from array to object", 0.7));
                else if (pType == "object" && cType == "array")
                {
                    var idx = failedPath.IndexOf(targetKey, StringComparison.Ordinal) + targetKey.Length;
                    suggestions.Add(new(failedPath, failedPath.Insert(idx, "[*]"),
                        $"'{targetKey}' changed from object to array", 0.7));
                }
            }

            if (suggestions.Count > 0)
                logger.LogInformation("Auto-heal found {Count} suggestion(s) for watch {WatchId}, path {Path}",
                    suggestions.Count, watchId, failedPath);

            return suggestions;
        }
    }

    public async Task RecordJsonStructureAsync(
        Guid watchId, string jsonSample, IBlockStateStore stateStore, CancellationToken ct = default)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(jsonSample); }
        catch { return; } // Intentional: unparseable sample = nothing to analyze

        using (doc)
        {
            var fp = BuildFingerprint(doc.RootElement);
            var json = JsonSerializer.SerializeToElement(fp);
            await stateStore.SaveOutputAsync(
                watchId.ToString(), $"reliability:json-structure:{watchId}", json, ct: ct);
        }
    }

    public async Task<int> GetAutoHealAttemptsAsync(
        Guid watchId, IBlockStateStore stateStore, CancellationToken ct = default)
    {
        var el = await stateStore.GetPreviousOutputAsync(
            watchId.ToString(), $"reliability:autoheal-attempts:{watchId}", ct);
        return el?.ValueKind == JsonValueKind.Number ? el.Value.GetInt32() : 0;
    }

    public async Task IncrementAutoHealAttemptAsync(
        Guid watchId, IBlockStateStore stateStore, CancellationToken ct = default)
    {
        var current = await GetAutoHealAttemptsAsync(watchId, stateStore, ct);
        var json = JsonSerializer.SerializeToElement(current + 1);
        await stateStore.SaveOutputAsync(
            watchId.ToString(), $"reliability:autoheal-attempts:{watchId}", json, ct: ct);
    }

    // ──────────────────────── Fingerprinting ────────────────────────

    internal static JsonStructureFingerprint BuildFingerprint(JsonElement root)
    {
        var keys = new List<string>();
        var types = new Dictionary<string, string>();
        int maxDepth = 0, nodeCount = 0;

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                keys.Add(prop.Name);
                types[prop.Name] = ValueKindLabel(prop.Value.ValueKind);
            }
        }

        WalkDepth(root, 0, ref maxDepth, ref nodeCount);
        return new(keys.AsReadOnly(), types, maxDepth, nodeCount);
    }

    private static void WalkDepth(JsonElement el, int depth, ref int maxDepth, ref int count)
    {
        count++;
        if (depth > maxDepth) maxDepth = depth;
        if (depth >= MaxFingerprintDepth) return;

        if (el.ValueKind == JsonValueKind.Object)
            foreach (var p in el.EnumerateObject())
                WalkDepth(p.Value, depth + 1, ref maxDepth, ref count);
        else if (el.ValueKind == JsonValueKind.Array)
            foreach (var item in el.EnumerateArray())
            {
                WalkDepth(item, depth + 1, ref maxDepth, ref count);
                break; // sample first element only
            }
    }

    private static string ValueKindLabel(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array  => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "null"
    };

    // ─────────────────────── Path Helpers ───────────────────────

    private static string? ExtractTargetKey(string jsonPath)
    {
        // "$.jobPostings[*].title" → "jobPostings"
        var span = jsonPath.AsSpan();
        if (span.StartsWith("$.")) span = span[2..];
        else if (span.StartsWith("$")) span = span[1..];

        var dot = span.IndexOfAny('.', '[');
        return dot > 0 ? span[..dot].ToString()
             : span.Length > 0 ? span.ToString()
             : null;
    }

    private static string? FindKeyPath(JsonElement el, string key, string prefix, int remaining)
    {
        if (remaining <= 0 || el.ValueKind != JsonValueKind.Object) return null;

        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Name == key)
                return $"{prefix}.{key}";

            var deeper = FindKeyPath(prop.Value, key, $"{prefix}.{prop.Name}", remaining - 1);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    private async Task<JsonStructureFingerprint?> LoadFingerprintAsync(
        Guid watchId, IBlockStateStore stateStore, CancellationToken ct)
    {
        var el = await stateStore.GetPreviousOutputAsync(
            watchId.ToString(), $"reliability:json-structure:{watchId}", ct);
        if (el is null) return null;

        try { return JsonSerializer.Deserialize<JsonStructureFingerprint>(el.Value.GetRawText()); }
        catch { return null; } // Intentional: corrupted fingerprint = treat as missing
    }
}
