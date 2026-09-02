using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Core.Pipeline.Validation;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.Pipeline;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.SetupPipeline;

public partial class ComposableSetupPipeline
{
    private async Task<DryRunResult> ExecuteDryRunAsync(PipelineDefinition pipeline, SetupSession session, CancellationToken ct)
    {
        try
        {
            var tempWatchId = Guid.NewGuid();
            var stateStore = new InMemoryBlockStateStore();
            var dryRunPipeline = CreateDryRunPipeline(pipeline, session);

            var result = await pipelineExecutor.ExecuteAsync(
                dryRunPipeline, tempWatchId, stateStore, null, ct, isDryRun: true);

            if (result.Success && IsDryRunOutputEmpty(result.OutputData))
            {
                logger.LogInformation("Dry run produced empty output, retrying after 2s delay...");
                await Task.Delay(2000, ct);
                result = await pipelineExecutor.ExecuteAsync(
                    dryRunPipeline, tempWatchId, stateStore, null, ct, isDryRun: true);
            }

            var blockOutputs = new Dictionary<string, object?>();
            foreach (var (blockId, blockResult) in result.BlockResults)
            {
                blockOutputs[blockId] = blockResult.Output?.ValueKind == JsonValueKind.Undefined
                    ? null
                    : blockResult.Output?.ToString();
            }

            var sampleOutput = result.OutputData?.ValueKind != JsonValueKind.Undefined
                ? result.OutputData?.ToString()
                : null;

            // Truncate sample output for display
            if (sampleOutput?.Length > 500)
                sampleOutput = sampleOutput[..500] + "... (truncated)";

            return new DryRunResult
            {
                Success = result.Success,
                BlockOutputs = blockOutputs,
                ExecutionDurationMs = result.ExecutionDurationMs,
                SkippedBlockIds = [.. result.SkippedBlockIds],
                WasBaseline = result.WasBaseline,
                SampleOutput = sampleOutput,
                Error = result.Error,
                BlockFlowSummary = BuildBlockFlowSummary(result, dryRunPipeline)
            };
        }
        catch (Exception ex)
        {
            return new DryRunResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    private static PipelineDefinition CreateDryRunPipeline(PipelineDefinition pipeline, SetupSession session)
    {
        if (string.IsNullOrWhiteSpace(session.FetchedHtml))
            return pipeline;

        var blocks = pipeline.Blocks.Select(block =>
        {
            if (!string.Equals(block.Type, "Navigate", StringComparison.OrdinalIgnoreCase))
                return block;

            var config = block.Config is { ValueKind: JsonValueKind.Object } existingConfig
                ? JsonNode.Parse(existingConfig.GetRawText())!.AsObject()
                : new JsonObject();

            config["_cachedHtml"] = session.FetchedHtml;
            return block with { Config = JsonSerializer.SerializeToElement(config) };
        }).ToList();

        return pipeline with { Blocks = blocks };
    }

    private async Task<AdversarialTestResult> RunAdversarialTestAsync(
        PipelineDefinition pipeline, DryRunResult dryRun, ParsedIntent intent, CancellationToken ct)
    {
        if (!dryRun.Success)
        {
            return new AdversarialTestResult
            {
                Passed = true,
                Skipped = true,
                SkipReason = "Dry run failed — adversarial testing requires a working pipeline."
            };
        }

        var hasLargeModel = await llmChain.HasLargeModelAsync(ct);
        if (!hasLargeModel)
        {
            return new AdversarialTestResult
            {
                Passed = true,
                Skipped = true,
                SkipReason = "No large model available — adversarial testing requires a large model."
            };
        }

        var blockSummary = string.Join("\n",
            pipeline.Blocks.Select(b => $"  - {b.Id} ({b.Type})"));

        var prompt = $$"""
            You are an adversarial tester for website monitoring pipelines.

            Pipeline blocks:
            {{blockSummary}}

            User intent: {{intent.Intent}}
            URL: {{intent.Url}}

            Imagine 3 realistic mutations that could happen to the target page:
            1. A CSS class or element ID is renamed
            2. The data format changes (e.g. currency symbol, date format)
            3. The page is completely redesigned with new structure

            For each mutation, predict which pipeline blocks would break (become "fragile").
            A block is fragile if it relies on specific selectors, class names, or structure
            that would change under the mutation.

            Respond with a JSON object:
            {
              "mutations": [
                {
                  "description": "short description of the mutation",
                  "predictedFragileBlocks": ["block-id-1", "block-id-2"]
                }
              ]
            }

            Respond ONLY with the JSON object.
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            ExpectJson = true,
            Temperature = 0.3f,
            MaxTokens = 512,
            UsageType = LlmUsageType.AdversarialTest,
            PreferLargeModel = true
        }, ct);

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content))
        {
            return new AdversarialTestResult
            {
                Passed = true,
                Skipped = true,
                SkipReason = $"LLM call failed: {response.ErrorMessage ?? "empty response"}"
            };
        }

        try
        {
            var analysis = DeserializeOrThrow<MutationAnalysis>(response.Content, "MutationAnalysis");
            var allFragile = analysis.Mutations
                .SelectMany(m => m.PredictedFragileBlocks ?? [])
                .Distinct()
                .ToList();

            var mutationsPassed = analysis.Mutations.Count(m => (m.PredictedFragileBlocks ?? []).Count == 0);

            return new AdversarialTestResult
            {
                Passed = allFragile.Count == 0,
                MutationsTested = analysis.Mutations.Count,
                MutationsPassed = mutationsPassed,
                FragileBlocks = allFragile,
                Warnings = analysis.Mutations
                    .Where(m => (m.PredictedFragileBlocks ?? []).Count > 0)
                    .Select(m => $"{m.Description}: {string.Join(", ", m.PredictedFragileBlocks!)}")
                    .ToList()
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse adversarial test response");
            return new AdversarialTestResult
            {
                Passed = true,
                Skipped = true,
                SkipReason = $"Failed to parse response: {ex.Message}"
            };
        }
    }

    private static string BuildHumanSummary(
        ParsedIntent intent, PipelineDefinition pipeline, DryRunResult dryRun, QcResult qc,
        AdversarialTestResult? adversarial = null)
    {
        var parts = new List<string>
        {
            $"**Monitoring:** {intent.Url}",
            $"**Intent:** {intent.Intent}",
            $"**Pipeline:** {string.Join(" → ", pipeline.Blocks.Select(b => b.Type))} ({pipeline.Blocks.Count} blocks)"
        };

        if (dryRun.Success && HasUsableDryRunOutput(dryRun))
            parts.Add("**Dry run:** ✓ Pipeline executed successfully");
        else if (dryRun.Success)
        {
            parts.Add("**Dry run:** ⚠ Pipeline executed but extracted no data");
            parts.Add("**Why this matters:** The page may need JavaScript rendering or a cookie banner may be blocking extraction.");
        }
        else
            parts.Add($"**Dry run:** ✗ {dryRun.Error ?? "Failed"}");

        if (!string.IsNullOrWhiteSpace(dryRun.BlockFlowSummary))
            parts.Add(dryRun.BlockFlowSummary);

        if (qc.Valid)
            parts.Add("**Quality check:** ✓ Passed");
        else
            parts.Add($"**Quality check:** ✗ {string.Join(", ", qc.Issues)}");

        if (qc.Suggestions.Count > 0)
            parts.Add($"**Suggestions:** {string.Join(", ", qc.Suggestions)}");

        if (qc.UnjustifiedBlocks.Count > 0)
            parts.Add($"**Unjustified blocks:** {string.Join(", ", qc.UnjustifiedBlocks)}");

        if (adversarial is not null && !adversarial.Skipped)
        {
            if (adversarial.Passed)
                parts.Add("**Adversarial test:** ✓ Pipeline is resilient to mutations");
            else
                parts.Add($"**Adversarial test:** ⚠ {adversarial.FragileBlocks.Count} fragile block(s): {string.Join(", ", adversarial.FragileBlocks)}");
        }
        else if (adversarial is { Skipped: true })
        {
            parts.Add($"**Adversarial test:** Skipped — {adversarial.SkipReason}");
        }

        return string.Join("\n", parts);
    }

    private static string BuildBlockFlowSummary(
        PipelineExecutionResult result, PipelineDefinition pipeline)
    {
        var sb = new StringBuilder();
        sb.AppendLine("📊 Block Flow:");

        // Build a lookup from block ID to block type for readable labels
        var blockTypeById = pipeline.Blocks.ToDictionary(b => b.Id, b => b.Type, StringComparer.OrdinalIgnoreCase);

        foreach (var (blockId, blockResult) in result.BlockResults)
        {
            var icon = blockResult.Status switch
            {
                BlockExecutionStatus.Completed => "✅",
                BlockExecutionStatus.Failed => "❌",
                BlockExecutionStatus.Skipped => "⚠️",
                BlockExecutionStatus.Baseline => "⏳",
                _ => "❓"
            };

            var blockType = blockTypeById.TryGetValue(blockId, out var t) ? t : blockId;

            var detail = BuildBlockDetail(blockResult);
            sb.AppendLine($"  {icon} {blockType} → {detail}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildBlockDetail(BlockResult blockResult)
    {
        if (blockResult.Status == BlockExecutionStatus.Skipped)
            return blockResult.SkipReason ?? "skipped";

        if (blockResult.Status == BlockExecutionStatus.Baseline)
            return DescribeOutput(blockResult, suffix: " (baseline captured)");

        if (!blockResult.Success)
            return blockResult.Error ?? "failed";

        return DescribeOutput(blockResult);
    }

    private static string DescribeOutput(BlockResult blockResult, string? suffix = null)
    {
        if (!blockResult.Output.HasValue || blockResult.Output.Value.ValueKind == JsonValueKind.Undefined)
            return blockResult.Success ? "no output" + (suffix ?? "") : "empty";

        var output = blockResult.Output.Value;
        string detail;

        if (output.ValueKind == JsonValueKind.Array)
        {
            detail = $"{output.GetArrayLength()} items";
        }
        else if (output.ValueKind == JsonValueKind.Object)
        {
            if (output.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                detail = $"{items.GetArrayLength()} items";
            else if (output.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                detail = $"{data.GetArrayLength()} items";
            else if (output.TryGetProperty("body", out var body))
                detail = $"response received ({body.GetString()?.Length ?? 0} chars)";
            else
                detail = "data received";
        }
        else
        {
            detail = "data received";
        }

        return detail + (suffix ?? "");
    }

    private static bool IsDeterministicTemplateSource(string? analysisSource)
        => analysisSource?.StartsWith("deterministic-template:", StringComparison.OrdinalIgnoreCase) == true;

    private static bool HasUsableDryRunOutput(DryRunResult? dryRunResult)
        => dryRunResult is { Success: true } && !IsDryRunOutputEmpty(dryRunResult);

    /// <summary>
    /// Known generic/placeholder CSS selectors that LLMs hallucinate when they can't identify
    /// real selectors from the HTML. If ALL selectors in the pipeline match this list,
    /// the pipeline is almost certainly fabricated.
    /// </summary>
    private static readonly HashSet<string> PlaceholderSelectors = new(StringComparer.OrdinalIgnoreCase)
    {
        ".job-card", ".job-listing", ".result-item", ".search-result",
        "li.result", "div.job", ".listing", ".vacancy",
        "table.jobs", ".careers-list", ".job-item", ".position",
        ".job", ".results", ".items", ".card"
    };

    /// <summary>
    /// Guard 3: Checks whether a pipeline's ExtractSchema blocks use only generic placeholder
    /// selectors (none page-specific). Returns true if ALL selectors are placeholders.
    /// Returns false (passes) if there are no ExtractSchema blocks or at least one page-specific selector.
    /// </summary>
    private static bool HasOnlyPlaceholderSelectors(PipelineDefinition pipeline)
    {
        var extractBlocks = pipeline.Blocks
            .Where(b => string.Equals(b.Type, "ExtractSchema", StringComparison.OrdinalIgnoreCase)
                        && b.Config is not null)
            .ToList();

        if (extractBlocks.Count == 0)
            return false; // No extraction blocks — not applicable

        var allSelectors = new List<string>();

        foreach (var block in extractBlocks)
        {
            var config = block.Config!.Value;

            // Collect the scope selector
            if (config.TryGetProperty("scope", out var scopeEl)
                && scopeEl.ValueKind == JsonValueKind.String
                && scopeEl.GetString() is { Length: > 0 } scope)
            {
                allSelectors.Add(scope);
            }

            // Collect field selectors from schema array
            if (config.TryGetProperty("schema", out var schemaEl)
                && schemaEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var field in schemaEl.EnumerateArray())
                {
                    if (field.TryGetProperty("selector", out var selEl)
                        && selEl.ValueKind == JsonValueKind.String
                        && selEl.GetString() is { Length: > 0 } selector)
                    {
                        allSelectors.Add(selector);
                    }
                }
            }
        }

        if (allSelectors.Count == 0)
            return false; // No selectors found — let other guards catch this

        return allSelectors.All(s => PlaceholderSelectors.Contains(s.Trim()));
    }

    private static bool IsDryRunOutputEmpty(JsonElement? outputData)
    {
        if (outputData is null or { ValueKind: JsonValueKind.Undefined })
            return true;

        return IsEffectivelyEmpty(outputData.Value);
    }

    private static bool IsDryRunOutputEmpty(DryRunResult dryRunResult)
    {
        if (!dryRunResult.Success)
            return false;

        if (string.IsNullOrWhiteSpace(dryRunResult.SampleOutput))
            return true;

        var sampleOutput = dryRunResult.SampleOutput.Trim();
        if (sampleOutput is "[]" or "{}" or "null")
            return true;

        try
        {
            using var document = JsonDocument.Parse(sampleOutput);
            return IsEffectivelyEmpty(document.RootElement);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsEffectivelyEmpty(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.Undefined => true,
            JsonValueKind.String => string.IsNullOrWhiteSpace(element.GetString()),
            JsonValueKind.Array => element.GetArrayLength() == 0,
            JsonValueKind.Object => element.EnumerateObject().ToList() is var properties &&
                                    (properties.Count == 0 || properties.All(property => IsEffectivelyEmpty(property.Value))),
            _ => false
        };
    }

    private static QcResult CreateUserFacingQcResult(QcResult qcResult)
    {
        var simplifiedIssues = qcResult.Issues
            .Select(SimplifyQcMessage)
            .Where(issue => !string.IsNullOrWhiteSpace(issue))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return qcResult with
        {
            Issues = simplifiedIssues
        };
    }

    private static string SimplifyQcMessage(string technicalMessage)
    {
        if (technicalMessage.Contains("pagination", StringComparison.OrdinalIgnoreCase))
            return "⚠️ This will only monitor the first page of results. Some older listings may be missed.";

        if (technicalMessage.Contains("empty list", StringComparison.OrdinalIgnoreCase) ||
            technicalMessage.Contains("empty array", StringComparison.OrdinalIgnoreCase))
            return "⚠️ The initial test didn't find any data. The page may have a cookie wall or require JavaScript.";

        if (technicalMessage.Contains("RelevanceScore", StringComparison.OrdinalIgnoreCase) &&
            technicalMessage.Contains("not clearly aligned", StringComparison.OrdinalIgnoreCase))
            return "ℹ️ The relevance scoring uses default keywords. You can customize these after creation.";

        if (technicalMessage.Contains("stable key", StringComparison.OrdinalIgnoreCase) ||
            technicalMessage.Contains("normalization", StringComparison.OrdinalIgnoreCase))
            return "ℹ️ Minor changes in job listing order might trigger false alerts initially.";

        if (technicalMessage.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
            technicalMessage.Contains("consent", StringComparison.OrdinalIgnoreCase))
            return "⚠️ This site has a cookie consent banner that may block content extraction.";

        var firstSentence = technicalMessage.Split('.')[0];
        return firstSentence.Length > 100 ? firstSentence[..100] + "..." : firstSentence;
    }

    private static TimeSpan ParseFrequency(string? frequency)
    {
        if (string.IsNullOrWhiteSpace(frequency))
            return TimeSpan.FromMinutes(30);

        frequency = frequency.Trim().ToLowerInvariant();

        if (frequency.EndsWith('m') && int.TryParse(frequency[..^1], out var minutes))
            return TimeSpan.FromMinutes(Math.Max(1, minutes));

        if (frequency.EndsWith('h') && int.TryParse(frequency[..^1], out var hours))
            return TimeSpan.FromHours(Math.Max(1, hours));

        if (frequency.EndsWith('d') && int.TryParse(frequency[..^1], out var days))
            return TimeSpan.FromDays(Math.Max(1, days));

        return TimeSpan.FromMinutes(30);
    }

    private DetectedPlatform? DetectPlatform(string url, string? html)
        => platformDetector.DetectFromContent(url, html ?? string.Empty);

}
