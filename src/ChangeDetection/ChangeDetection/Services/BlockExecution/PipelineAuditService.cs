using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.BlockExecution;

public enum PipelineChangeType
{
    NoChange,
    ConfigOnly,
    Structural,
    UrlsChanged
}

public record PipelineChangeEvent(
    Guid WatchId,
    string PreviousFingerprint,
    string NewFingerprint,
    PipelineChangeType ChangeType,
    IReadOnlyList<string> Changes,
    bool RequiresApproval,
    DateTime DetectedAt);

public record AuditEntry(
    Guid WatchId,
    string Event,
    DateTime Timestamp,
    Dictionary<string, string> Details);

public class PipelineAuditService(ILogger<PipelineAuditService> logger)
{
    // ── Change Detection ───────────────────────────────────────────────

    public PipelineChangeEvent DetectChanges(
        Guid watchId,
        PipelineDefinition? oldPipeline,
        PipelineDefinition newPipeline)
    {
        var newFingerprint = ComputeFingerprint(newPipeline);

        if (oldPipeline is null)
        {
            return new PipelineChangeEvent(
                watchId,
                PreviousFingerprint: "",
                NewFingerprint: newFingerprint,
                PipelineChangeType.Structural,
                Changes: ["Initial pipeline creation"],
                RequiresApproval: false,   // first creation doesn't need approval
                DetectedAt: DateTime.UtcNow);
        }

        var oldFingerprint = ComputeFingerprint(oldPipeline);
        if (oldFingerprint == newFingerprint)
        {
            return new PipelineChangeEvent(
                watchId, oldFingerprint, newFingerprint,
                PipelineChangeType.NoChange,
                Changes: [],
                RequiresApproval: false,
                DetectedAt: DateTime.UtcNow);
        }

        var changes = new List<string>();
        var changeType = ClassifyChanges(oldPipeline, newPipeline, changes);

        var evt = new PipelineChangeEvent(
            watchId, oldFingerprint, newFingerprint, changeType, changes,
            RequiresApproval: changeType is PipelineChangeType.Structural
                                         or PipelineChangeType.UrlsChanged,
            DetectedAt: DateTime.UtcNow);

        if (evt.RequiresApproval)
        {
            logger.LogWarning(
                "Pipeline change requires approval for watch {WatchId}: {ChangeType} — {Changes}",
                watchId, changeType, string.Join("; ", changes));
        }
        else
        {
            logger.LogInformation(
                "Pipeline config-only change detected for watch {WatchId}: {Changes}",
                watchId, string.Join("; ", changes));
        }

        return evt;
    }

    // ── Audit Logging ──────────────────────────────────────────────────

    public void LogPipelineCreated(
        Guid watchId, string userInput, string contentHash,
        string pipelineFingerprint, DomainPin pin)
    {
        var entry = new AuditEntry(watchId, "pipeline_created", DateTime.UtcNow,
            new Dictionary<string, string>
            {
                ["userInput"] = Truncate(userInput, 200),
                ["contentHash"] = contentHash,
                ["pipelineFingerprint"] = pipelineFingerprint,
                ["pinnedDomain"] = pin.PrimaryDomain,
                ["allowedSchemes"] = string.Join(",", pin.AllowedSchemes)
            });

        logger.LogInformation(
            "Pipeline created for watch {WatchId} | fingerprint={Fingerprint} domain={Domain}",
            watchId, pipelineFingerprint, pin.PrimaryDomain);

        EmitStructured(entry);
    }

    public void LogPipelineValidated(Guid watchId, SecurityValidationResult result)
    {
        logger.LogInformation(
            "Pipeline validated for watch {WatchId} | fingerprint={Fingerprint} warnings={WarningCount}",
            watchId, result.PipelineFingerprint, result.Warnings.Count);

        EmitStructured(new AuditEntry(watchId, "pipeline_validated", DateTime.UtcNow,
            new Dictionary<string, string>
            {
                ["pipelineFingerprint"] = result.PipelineFingerprint,
                ["warningCount"] = result.Warnings.Count.ToString()
            }));
    }

    public void LogPipelineRejected(Guid watchId, SecurityValidationResult result)
    {
        logger.LogWarning(
            "Pipeline REJECTED for watch {WatchId} | fingerprint={Fingerprint} violations={Violations}",
            watchId, result.PipelineFingerprint,
            string.Join("; ", result.Violations.Select(v => $"{v.Rule}: {v.Detail}")));

        EmitStructured(new AuditEntry(watchId, "pipeline_rejected", DateTime.UtcNow,
            new Dictionary<string, string>
            {
                ["pipelineFingerprint"] = result.PipelineFingerprint,
                ["violationCount"] = result.Violations.Count.ToString(),
                ["violations"] = JsonSerializer.Serialize(
                    result.Violations.Select(v => new { v.Rule, v.BlockId, v.Detail }))
            }));
    }

    public void LogPipelineExecuted(
        Guid watchId, string pipelineFingerprint,
        int durationMs, bool success, string? error)
    {
        if (success)
        {
            logger.LogInformation(
                "Pipeline executed for watch {WatchId} | fingerprint={Fingerprint} duration={Duration}ms",
                watchId, pipelineFingerprint, durationMs);
        }
        else
        {
            logger.LogWarning(
                "Pipeline execution failed for watch {WatchId} | fingerprint={Fingerprint} duration={Duration}ms error={Error}",
                watchId, pipelineFingerprint, durationMs, error);
        }

        EmitStructured(new AuditEntry(watchId, "pipeline_executed", DateTime.UtcNow,
            new Dictionary<string, string>
            {
                ["pipelineFingerprint"] = pipelineFingerprint,
                ["durationMs"] = durationMs.ToString(),
                ["success"] = success.ToString(),
                ["error"] = error ?? ""
            }));
    }

    public void LogSecurityViolation(
        Guid watchId, string violationType, string detail, string? blockedUrl = null)
    {
        logger.LogError(
            "SECURITY VIOLATION for watch {WatchId} | type={ViolationType} detail={Detail} blockedUrl={BlockedUrl}",
            watchId, violationType, detail, blockedUrl ?? "(none)");

        var details = new Dictionary<string, string>
        {
            ["violationType"] = violationType,
            ["detail"] = detail
        };
        if (blockedUrl is not null)
            details["blockedUrl"] = blockedUrl;

        EmitStructured(new AuditEntry(watchId, "security_violation", DateTime.UtcNow, details));
    }

    public void LogResourceExhausted(Guid watchId, string resource, string detail)
    {
        logger.LogError(
            "Resource exhausted for watch {WatchId} | resource={Resource} detail={Detail}",
            watchId, resource, detail);

        EmitStructured(new AuditEntry(watchId, "resource_exceeded", DateTime.UtcNow,
            new Dictionary<string, string>
            {
                ["resource"] = resource,
                ["detail"] = detail
            }));
    }

    // ── Provenance ─────────────────────────────────────────────────────

    public AuditEntry CreateProvenanceEntry(
        Guid watchId, string userInput,
        string rawContentHash, string sanitizedContentHash,
        string pipelineFingerprint, DomainPin pin)
    {
        var entry = new AuditEntry(watchId, "pipeline_created", DateTime.UtcNow,
            new Dictionary<string, string>
            {
                ["userInput"] = Truncate(userInput, 200),
                ["rawContentHash"] = rawContentHash,
                ["sanitizedContentHash"] = sanitizedContentHash,
                ["pipelineFingerprint"] = pipelineFingerprint,
                ["pinnedDomain"] = pin.PrimaryDomain,
                ["allowedPatterns"] = string.Join(",", pin.AllowedPatterns),
                ["allowedSchemes"] = string.Join(",", pin.AllowedSchemes)
            });

        logger.LogInformation(
            "Provenance recorded for watch {WatchId} | rawHash={RawHash} sanitizedHash={SanitizedHash} fingerprint={Fingerprint} domain={Domain}",
            watchId, rawContentHash, sanitizedContentHash,
            pipelineFingerprint, pin.PrimaryDomain);

        return entry;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static PipelineChangeType ClassifyChanges(
        PipelineDefinition oldPipeline,
        PipelineDefinition newPipeline,
        List<string> changes)
    {
        var highestChange = PipelineChangeType.NoChange;

        // 1. Block count difference
        if (oldPipeline.Blocks.Count != newPipeline.Blocks.Count)
        {
            changes.Add($"Block count changed: {oldPipeline.Blocks.Count} → {newPipeline.Blocks.Count}");
            highestChange = PipelineChangeType.Structural;
        }

        // 2. Block identity (IDs + types)
        var oldBlockIds = oldPipeline.Blocks.Select(b => b.Id).ToHashSet();
        var newBlockIds = newPipeline.Blocks.Select(b => b.Id).ToHashSet();

        var addedBlocks = newBlockIds.Except(oldBlockIds).ToList();
        var removedBlocks = oldBlockIds.Except(newBlockIds).ToList();

        if (addedBlocks.Count > 0)
        {
            changes.Add($"Blocks added: {string.Join(", ", addedBlocks)}");
            highestChange = PipelineChangeType.Structural;
        }

        if (removedBlocks.Count > 0)
        {
            changes.Add($"Blocks removed: {string.Join(", ", removedBlocks)}");
            highestChange = PipelineChangeType.Structural;
        }

        // Check type changes for blocks that exist in both
        var commonBlockIds = oldBlockIds.Intersect(newBlockIds);
        var oldBlockMap = oldPipeline.Blocks.ToDictionary(b => b.Id);
        var newBlockMap = newPipeline.Blocks.ToDictionary(b => b.Id);

        foreach (var id in commonBlockIds)
        {
            if (!string.Equals(oldBlockMap[id].Type, newBlockMap[id].Type, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add($"Block '{id}' type changed: {oldBlockMap[id].Type} → {newBlockMap[id].Type}");
                highestChange = PipelineChangeType.Structural;
            }
        }

        // 3. Connection topology
        var oldConnections = oldPipeline.Connections
            .Select(c => $"{c.FromBlockId}:{c.FromPort}->{c.ToBlockId}:{c.ToPort}")
            .ToHashSet();
        var newConnections = newPipeline.Connections
            .Select(c => $"{c.FromBlockId}:{c.FromPort}->{c.ToBlockId}:{c.ToPort}")
            .ToHashSet();

        if (!oldConnections.SetEquals(newConnections))
        {
            var addedConns = newConnections.Except(oldConnections).ToList();
            var removedConns = oldConnections.Except(newConnections).ToList();

            if (addedConns.Count > 0)
                changes.Add($"Connections added: {string.Join(", ", addedConns)}");
            if (removedConns.Count > 0)
                changes.Add($"Connections removed: {string.Join(", ", removedConns)}");

            if (highestChange < PipelineChangeType.Structural)
                highestChange = PipelineChangeType.Structural;
        }

        // If already structural, no need to check lower-severity changes
        if (highestChange == PipelineChangeType.Structural)
            return highestChange;

        // 4. URL changes in block configs
        var oldUrls = ExtractUrls(oldPipeline);
        var newUrls = ExtractUrls(newPipeline);

        if (!oldUrls.SetEquals(newUrls))
        {
            var addedUrls = newUrls.Except(oldUrls).ToList();
            var removedUrls = oldUrls.Except(newUrls).ToList();

            if (addedUrls.Count > 0)
                changes.Add($"URLs added: {string.Join(", ", addedUrls)}");
            if (removedUrls.Count > 0)
                changes.Add($"URLs removed: {string.Join(", ", removedUrls)}");

            return PipelineChangeType.UrlsChanged;
        }

        // 5. Config-only changes (selectors, JSONPaths, etc.)
        foreach (var id in commonBlockIds)
        {
            var oldConfig = SerializeConfig(oldBlockMap[id].Config);
            var newConfig = SerializeConfig(newBlockMap[id].Config);

            if (oldConfig != newConfig)
            {
                changes.Add($"Block '{id}' config changed");
                highestChange = PipelineChangeType.ConfigOnly;
            }
        }

        return highestChange;
    }

    private static HashSet<string> ExtractUrls(PipelineDefinition pipeline)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var block in pipeline.Blocks)
        {
            if (block.Config is not { } config)
                continue;

            ExtractUrlsFromElement(config, urls);
        }

        return urls;
    }

    private static void ExtractUrlsFromElement(JsonElement element, HashSet<string> urls)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (value is not null && Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    && uri.Scheme is "http" or "https")
                {
                    urls.Add(value);
                }
                break;

            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                    ExtractUrlsFromElement(prop.Value, urls);
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    ExtractUrlsFromElement(item, urls);
                break;
        }
    }

    private static string SerializeConfig(JsonElement? config) =>
        config is { } c ? c.GetRawText() : "";

    private static string ComputeFingerprint(PipelineDefinition pipeline)
    {
        var json = JsonSerializer.Serialize(pipeline, JsonContext.Default);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(hash);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";

    private void EmitStructured(AuditEntry entry)
    {
        // Emit as structured log so OpenTelemetry picks it up
        logger.LogDebug(
            "AuditEntry | watch={WatchId} event={Event} ts={Timestamp} details={Details}",
            entry.WatchId, entry.Event, entry.Timestamp,
            JsonSerializer.Serialize(entry.Details));
    }

    // Matches the fingerprint computation in PipelineSecurityValidator
    private static class JsonContext
    {
        public static readonly JsonSerializerOptions Default = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
}
