using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.BlockExecution;

public enum SecuritySeverity { High, Critical }

public record SecurityViolation(
    string Rule,
    string BlockId,
    string Detail,
    SecuritySeverity Severity
);

public record SecurityWarning(
    string Rule,
    string BlockId,
    string Detail
);

public record SecurityValidationResult(
    bool IsValid,
    IReadOnlyList<SecurityViolation> Violations,
    IReadOnlyList<SecurityWarning> Warnings,
    string PipelineFingerprint
);

public record SecurityPolicy
{
    public int MaxBlocks { get; init; } = 20;
    public int MaxDagDepth { get; init; } = 5;
    public int MaxHttpRequestsPerRun { get; init; } = 200;
    public int MaxPlaywrightNavigations { get; init; } = 10;
    public long MaxResponseSizeBytes { get; init; } = 5 * 1024 * 1024;
    public int MaxExecutionTimeSeconds { get; init; } = 120;
}

/// <summary>
/// Hard security gate — validates every LLM-generated pipeline before execution.
/// Complements <see cref="PipelineValidator"/> (structural) with security policy enforcement:
/// block allowlisting, domain pinning, data-flow analysis, template safety, and budget limits.
/// </summary>
public partial class PipelineSecurityValidator(
    DomainPinValidator domainPinValidator,
    ILogger<PipelineSecurityValidator> logger)
{
    private static readonly SecurityPolicy DefaultPolicy = new();

    private static readonly HashSet<string> AllowedBlockTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Input", "Navigate", "Paginate", "Click", "Scroll", "Wait", "Search",
        "ExtractSchema", "DataFilter", "Filter", "VolatilityFilter",
        "TextDiff", "HashCompare", "ListDiff", "StructDiff", "NumericDelta", "RelevanceScore", "RankingSnapshot",
        "Condition", "Notify", "Route",
        "Aggregate", "LookupHistory", "LinkValidate", "Enrich", "Transform", "Throttle",
        "LlmExtract", "LlmCraftPrompt", "LlmEvaluate",
        "Output",
        "HttpRequest", "JsonExtract", "ForEachRequest", "Iterate"
    };

    /// <summary>Blocks whose output may carry ExtractedObjects data.</summary>
    private static readonly HashSet<string> ExtractionBlockTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ExtractSchema", "JsonExtract", "LlmExtract", "Filter", "DataFilter", "VolatilityFilter"
    };

    /// <summary>Blocks that make outbound HTTP/Playwright requests.</summary>
    private static readonly HashSet<string> HttpBlockTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Navigate", "HttpRequest", "Paginate", "Enrich", "ForEachRequest", "Iterate", "LinkValidate"
    };

    /// <summary>Input port names that accept a URL.</summary>
    private static readonly HashSet<string> UrlPortNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "url", "urlTemplate", "urlPattern", "baseUrl", "targetUrl"
    };

    /// <summary>Config field names that carry URLs (matches DomainPinValidator.UrlConfigFields).</summary>
    private static readonly HashSet<string> UrlConfigFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "url", "urlTemplate", "urlPattern", "baseUrl", "targetUrl", "redirectUrl"
    };

    private static readonly HashSet<string> PlaywrightBlockTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Navigate", "Click", "Scroll", "Wait", "Search", "Paginate"
    };

    private static readonly HashSet<string> PureHttpBlockTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "HttpRequest", "ForEachRequest", "Iterate", "Enrich", "LinkValidate"
    };

    [GeneratedRegex(@"\{\{(.+?)\}\}", RegexOptions.Compiled)]
    private static partial Regex TemplateRegex();

    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ────────────────────────────────────────────────────────────
    //  Public API
    // ────────────────────────────────────────────────────────────

    public SecurityValidationResult Validate(PipelineDefinition pipeline, DomainPin pin)
        => Validate(pipeline, pin, DefaultPolicy);

    public SecurityValidationResult Validate(PipelineDefinition pipeline, DomainPin pin, SecurityPolicy policy)
    {
        List<SecurityViolation> violations = [];
        List<SecurityWarning> warnings = [];

        var blockMap = BuildBlockMap(pipeline);

        CheckBlockAllowlist(pipeline, violations);
        CheckDomainPin(pipeline, pin, violations);
        CheckDataFlow(pipeline, blockMap, violations);
        CheckTemplateVariables(pipeline, violations, warnings);
        CheckStructuralConstraints(pipeline, blockMap, policy, violations);
        CheckBudgetLimits(pipeline, policy, warnings);

        var fingerprint = ComputeFingerprint(pipeline);
        var isValid = violations.Count == 0;

        if (!isValid)
        {
            logger.LogWarning(
                "Pipeline security validation FAILED: {ViolationCount} violation(s), {WarningCount} warning(s). Fingerprint: {Fingerprint}",
                violations.Count, warnings.Count, fingerprint);
        }
        else if (warnings.Count > 0)
        {
            logger.LogInformation(
                "Pipeline security validation passed with {WarningCount} warning(s). Fingerprint: {Fingerprint}",
                warnings.Count, fingerprint);
        }

        return new SecurityValidationResult(isValid, violations, warnings, fingerprint);
    }

    // ────────────────────────────────────────────────────────────
    //  1. Block Type Allowlist
    // ────────────────────────────────────────────────────────────

    private static void CheckBlockAllowlist(
        PipelineDefinition pipeline, List<SecurityViolation> violations)
    {
        foreach (var block in pipeline.Blocks)
        {
            if (!AllowedBlockTypes.Contains(block.Type))
            {
                violations.Add(new SecurityViolation(
                    "BLOCK_ALLOWLIST",
                    block.Id,
                    $"Block type '{block.Type}' is not in the security allowlist.",
                    SecuritySeverity.Critical));
            }
        }
    }

    // ────────────────────────────────────────────────────────────
    //  2. Domain Pin Validation
    // ────────────────────────────────────────────────────────────

    private void CheckDomainPin(
        PipelineDefinition pipeline, DomainPin pin, List<SecurityViolation> violations)
    {
        var pinViolations = domainPinValidator.ValidatePipeline(pipeline, pin);

        foreach (var (blockId, url, error) in pinViolations)
        {
            violations.Add(new SecurityViolation(
                "DOMAIN_PIN",
                blockId,
                $"URL '{url}' violates domain pin: {error}",
                SecuritySeverity.Critical));
        }
    }

    // ────────────────────────────────────────────────────────────
    //  3. Data Flow Analysis (MVP: direct connection check)
    // ────────────────────────────────────────────────────────────

    private static void CheckDataFlow(
        PipelineDefinition pipeline,
        Dictionary<string, BlockDefinition> blockMap,
        List<SecurityViolation> violations)
    {
        foreach (var conn in pipeline.Connections)
        {
            if (!blockMap.TryGetValue(conn.FromBlockId, out var sourceBlock) ||
                !blockMap.TryGetValue(conn.ToBlockId, out var targetBlock))
                continue;

            if (ExtractionBlockTypes.Contains(sourceBlock.Type) &&
                HttpBlockTypes.Contains(targetBlock.Type) &&
                UrlPortNames.Contains(conn.ToPort))
            {
                violations.Add(new SecurityViolation(
                    "DATA_FLOW",
                    conn.ToBlockId,
                    $"Extracted data flows from '{conn.FromBlockId}' ({sourceBlock.Type}) " +
                    $"into URL port '{conn.ToPort}' on '{conn.ToBlockId}' ({targetBlock.Type}). " +
                    "This could enable server-side request forgery via LLM-extracted content.",
                    SecuritySeverity.Critical));
            }
        }
    }

    // ────────────────────────────────────────────────────────────
    //  4. Template Variable Safety
    // ────────────────────────────────────────────────────────────

    private static void CheckTemplateVariables(
        PipelineDefinition pipeline,
        List<SecurityViolation> violations,
        List<SecurityWarning> warnings)
    {
        foreach (var block in pipeline.Blocks)
        {
            if (block.Config is not { ValueKind: JsonValueKind.Object } config)
                continue;

            ScanConfigForTemplates(block, config, violations, warnings);
        }
    }

    private static void ScanConfigForTemplates(
        BlockDefinition block,
        JsonElement config,
        List<SecurityViolation> violations,
        List<SecurityWarning> warnings)
    {
        var isForEachRequest = string.Equals(
            block.Type, "ForEachRequest", StringComparison.OrdinalIgnoreCase);

        foreach (var property in config.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;

            var value = property.Value.GetString();
            if (string.IsNullOrEmpty(value))
                continue;

            var matches = TemplateRegex().Matches(value);
            if (matches.Count == 0)
                continue;

            var isUrlField = UrlConfigFieldNames.Contains(property.Name);

            foreach (Match match in matches)
            {
                var templateContent = match.Groups[1].Value;
                var isItemTemplate = templateContent.StartsWith("item.", StringComparison.Ordinal);

                // {{item.xxx}} outside ForEachRequest = data injection risk
                if (isItemTemplate && !isForEachRequest)
                {
                    violations.Add(new SecurityViolation(
                        "TEMPLATE_SAFETY",
                        block.Id,
                        $"Template '{{{{{templateContent}}}}}' in field '{property.Name}' " +
                        "uses item data outside a ForEachRequest block. " +
                        "This could inject extracted content into requests.",
                        SecuritySeverity.High));
                    continue;
                }

                // Templates in URL fields = warning (except allowed {{item.}} in ForEachRequest)
                if (isUrlField && !(isItemTemplate && isForEachRequest))
                {
                    warnings.Add(new SecurityWarning(
                        "TEMPLATE_SAFETY",
                        block.Id,
                        $"Template '{{{{{templateContent}}}}}' in URL field '{property.Name}' " +
                        "could enable data exfiltration if it resolves to attacker-controlled content."));
                }
            }
        }
    }

    // ────────────────────────────────────────────────────────────
    //  5. Structural Constraints
    // ────────────────────────────────────────────────────────────

    private static void CheckStructuralConstraints(
        PipelineDefinition pipeline,
        Dictionary<string, BlockDefinition> blockMap,
        SecurityPolicy policy,
        List<SecurityViolation> violations)
    {
        // Max block count
        if (pipeline.Blocks.Count > policy.MaxBlocks)
        {
            violations.Add(new SecurityViolation(
                "STRUCTURAL",
                "",
                $"Pipeline has {pipeline.Blocks.Count} blocks, exceeding the maximum of {policy.MaxBlocks}.",
                SecuritySeverity.High));
        }

        // Exactly one Input block
        var inputBlocks = pipeline.Blocks
            .Where(b => string.Equals(b.Type, "Input", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (inputBlocks.Count != 1)
        {
            violations.Add(new SecurityViolation(
                "STRUCTURAL",
                "",
                inputBlocks.Count == 0
                    ? "Pipeline must have exactly one Input block."
                    : $"Pipeline has {inputBlocks.Count} Input blocks; exactly one is required.",
                SecuritySeverity.Critical));
        }

        // At least one Output block
        var outputCount = pipeline.Blocks.Count(b =>
            string.Equals(b.Type, "Output", StringComparison.OrdinalIgnoreCase));

        if (outputCount == 0)
        {
            violations.Add(new SecurityViolation(
                "STRUCTURAL",
                "",
                "Pipeline must have at least one Output block.",
                SecuritySeverity.Critical));
        }

        // Cycle detection (DFS with visited / in-stack sets)
        var adjacency = BuildAdjacency(pipeline);
        if (DetectCycle(blockMap, adjacency, violations))
            return; // DAG depth is meaningless when cycles exist

        // DAG depth (longest path from Input)
        if (inputBlocks.Count == 1)
        {
            var maxDepth = ComputeMaxDagDepth(inputBlocks[0].Id, blockMap, adjacency);
            if (maxDepth > policy.MaxDagDepth)
            {
                violations.Add(new SecurityViolation(
                    "STRUCTURAL",
                    "",
                    $"Pipeline DAG depth is {maxDepth}, exceeding the maximum of {policy.MaxDagDepth}.",
                    SecuritySeverity.High));
            }
        }
    }

    /// <summary>
    /// DFS-based cycle detection using visited / in-stack (gray/black) coloring.
    /// </summary>
    private static bool DetectCycle(
        Dictionary<string, BlockDefinition> blockMap,
        Dictionary<string, List<string>> adjacency,
        List<SecurityViolation> violations)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var inStack = new HashSet<string>(StringComparer.Ordinal);

        foreach (var blockId in blockMap.Keys)
        {
            if (!visited.Contains(blockId) &&
                DfsFindCycle(blockId, adjacency, visited, inStack))
            {
                violations.Add(new SecurityViolation(
                    "CYCLE",
                    "",
                    $"Pipeline contains a cycle involving block(s): {string.Join(", ", inStack)}.",
                    SecuritySeverity.Critical));
                return true;
            }
        }

        return false;
    }

    private static bool DfsFindCycle(
        string node,
        Dictionary<string, List<string>> adjacency,
        HashSet<string> visited,
        HashSet<string> inStack)
    {
        visited.Add(node);
        inStack.Add(node);

        if (adjacency.TryGetValue(node, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                if (inStack.Contains(neighbor))
                    return true;

                if (!visited.Contains(neighbor) &&
                    DfsFindCycle(neighbor, adjacency, visited, inStack))
                    return true;
            }
        }

        inStack.Remove(node);
        return false;
    }

    /// <summary>
    /// Computes the longest path (in edges) from the Input block via topological relaxation.
    /// </summary>
    private static int ComputeMaxDagDepth(
        string inputBlockId,
        Dictionary<string, BlockDefinition> blockMap,
        Dictionary<string, List<string>> adjacency)
    {
        var dist = new Dictionary<string, int>(StringComparer.Ordinal);
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var id in blockMap.Keys)
        {
            dist[id] = -1;
            inDegree[id] = 0;
        }

        dist[inputBlockId] = 0;

        foreach (var (_, neighbors) in adjacency)
        {
            foreach (var to in neighbors)
            {
                if (inDegree.ContainsKey(to))
                    inDegree[to]++;
            }
        }

        // Kahn's topological sort with longest-path relaxation
        var queue = new Queue<string>();
        foreach (var (id, deg) in inDegree)
        {
            if (deg == 0)
                queue.Enqueue(id);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            if (adjacency.TryGetValue(current, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                {
                    if (dist[current] >= 0)
                        dist[neighbor] = Math.Max(dist[neighbor], dist[current] + 1);

                    inDegree[neighbor]--;
                    if (inDegree[neighbor] == 0)
                        queue.Enqueue(neighbor);
                }
            }
        }

        var maxDepth = 0;
        foreach (var d in dist.Values)
        {
            if (d > maxDepth) maxDepth = d;
        }

        return maxDepth;
    }

    // ────────────────────────────────────────────────────────────
    //  6. Budget Limits
    // ────────────────────────────────────────────────────────────

    private static void CheckBudgetLimits(
        PipelineDefinition pipeline, SecurityPolicy policy, List<SecurityWarning> warnings)
    {
        // Playwright navigation blocks
        var playwrightCount = pipeline.Blocks.Count(b => PlaywrightBlockTypes.Contains(b.Type));
        if (playwrightCount > policy.MaxPlaywrightNavigations)
        {
            warnings.Add(new SecurityWarning(
                "BUDGET",
                "",
                $"Pipeline has {playwrightCount} Playwright-type blocks, " +
                $"exceeding the recommended maximum of {policy.MaxPlaywrightNavigations}."));
        }

        // HTTP request blocks — ForEachRequest/Iterate can fan out
        var httpCount = pipeline.Blocks.Count(b => PureHttpBlockTypes.Contains(b.Type));
        var estimatedRequests = httpCount;

        foreach (var block in pipeline.Blocks)
        {
            if (string.Equals(block.Type, "ForEachRequest", StringComparison.OrdinalIgnoreCase))
            {
                var maxItems = GetConfigInt(block, "maxItems") ?? 50;
                estimatedRequests += maxItems - 1; // already counted once in httpCount
                continue;
            }

            if (string.Equals(block.Type, "Iterate", StringComparison.OrdinalIgnoreCase))
            {
                var maxValues = GetConfigInt(block, "maxValues") ?? 50;
                estimatedRequests += maxValues - 1; // already counted once in httpCount
            }
        }

        if (estimatedRequests > policy.MaxHttpRequestsPerRun)
        {
            warnings.Add(new SecurityWarning(
                "BUDGET",
                "",
                $"Pipeline may make ~{estimatedRequests} HTTP requests, " +
                $"exceeding the maximum of {policy.MaxHttpRequestsPerRun}."));
        }
    }

    // ────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────

    private static Dictionary<string, BlockDefinition> BuildBlockMap(PipelineDefinition pipeline)
    {
        var map = new Dictionary<string, BlockDefinition>(StringComparer.Ordinal);
        foreach (var block in pipeline.Blocks)
            map.TryAdd(block.Id, block);
        return map;
    }

    private static Dictionary<string, List<string>> BuildAdjacency(PipelineDefinition pipeline)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var block in pipeline.Blocks)
            adjacency[block.Id] = [];

        foreach (var conn in pipeline.Connections)
        {
            if (adjacency.ContainsKey(conn.FromBlockId) &&
                adjacency.ContainsKey(conn.ToBlockId) &&
                !string.Equals(conn.FromBlockId, conn.ToBlockId, StringComparison.Ordinal))
            {
                adjacency[conn.FromBlockId].Add(conn.ToBlockId);
            }
        }

        return adjacency;
    }

    private static int? GetConfigInt(BlockDefinition block, string fieldName)
    {
        if (block.Config is not { ValueKind: JsonValueKind.Object } config)
            return null;

        return config.TryGetProperty(fieldName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;
    }

    /// <summary>
    /// SHA-256 fingerprint of the canonical pipeline JSON for audit logging.
    /// </summary>
    private static string ComputeFingerprint(PipelineDefinition pipeline)
    {
        var json = JsonSerializer.Serialize(pipeline, CanonicalJsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(hash);
    }
}
