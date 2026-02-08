using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.AutoHealing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static ChangeDetection.Core.Pipeline.PromptSanitizer;

namespace ChangeDetection.Services.AutoHealing;

/// <summary>
/// Progressive 3-layer auto-healing for failed pipeline blocks.
/// Layer 1: LLM suggests new block config (e.g., updated CSS selector).
/// Layer 2: LLM diagnoses by comparing current HTML vs setup-time snapshot.
/// Layer 3: Pause watch and notify user.
/// </summary>
public class AutoHealingService(
    ILlmProviderChain llmChain,
    IContentFetcher contentFetcher,
    ILogger<AutoHealingService> logger) : IAutoHealingService
{
    public async Task<HealingResult> AttemptHealAsync(HealingContext context, CancellationToken ct = default)
    {
        var thresholds = new HealingThresholds();

        if (context.ConsecutiveFailures < thresholds.Layer1Threshold)
        {
            return new HealingResult
            {
                Outcome = HealingOutcome.NoActionNeeded,
                Message = $"Below failure threshold ({context.ConsecutiveFailures}/{thresholds.Layer1Threshold})"
            };
        }

        // Layer 1: Block self-heal via LLM selector suggestion
        if (context.ConsecutiveFailures <= thresholds.Layer1Threshold + thresholds.Layer1MaxAttempts)
        {
            logger.LogInformation(
                "Layer 1: Attempting self-heal for block {BlockId} on watch {WatchId} (failure {Count})",
                context.BlockInstanceId, context.WatchId, context.ConsecutiveFailures);

            return await AttemptLayer1Async(context, ct);
        }

        // Layer 2: Pipeline-level diagnosis comparing HTML snapshots
        if (context.ConsecutiveFailures <= thresholds.Layer2Threshold + thresholds.Layer2MaxAttempts)
        {
            logger.LogInformation(
                "Layer 2: Attempting pipeline diagnosis for block {BlockId} on watch {WatchId} (failure {Count})",
                context.BlockInstanceId, context.WatchId, context.ConsecutiveFailures);

            return await AttemptLayer2Async(context, ct);
        }

        // Layer 3: Pause watch and notify user
        logger.LogWarning(
            "Layer 3: Auto-healing exhausted for block {BlockId} on watch {WatchId} after {Count} failures. Pausing watch.",
            context.BlockInstanceId, context.WatchId, context.ConsecutiveFailures);

        // Pause the watch
        WatchedSite? pausedWatch = null;
        try
        {
            var watchRepo = context.Services?.GetService<IRepository<WatchedSite>>();
            if (watchRepo is not null)
            {
                pausedWatch = await watchRepo.GetByIdAsync(context.WatchId, ct);
                if (pausedWatch is not null)
                {
                    pausedWatch.IsEnabled = false;
                    pausedWatch.Status = WatchStatus.Paused;
                    await watchRepo.UpdateAsync(pausedWatch, ct);
                    logger.LogInformation("Watch {WatchId} paused due to unrecoverable block failure", context.WatchId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to pause watch {WatchId}", context.WatchId);
        }

        // Notify user
        try
        {
            var notificationService = context.Services?.GetService<INotificationService>();
            if (notificationService is not null && pausedWatch is not null)
            {
                var summary = $"Watch paused: block '{context.BlockInstanceId}' failed {context.ConsecutiveFailures} times. Auto-healing exhausted.";
                var syntheticEvent = new ChangeEvent
                {
                    WatchedSiteId = context.WatchId,
                    OwnerId = pausedWatch.OwnerId,
                    DiffSummary = summary,
                    BriefSummary = $"Last error: {context.ErrorMessage}",
                    ChangeType = ChangeType.Unknown,
                    Importance = ChangeImportance.High,
                    DetectedAt = DateTime.UtcNow
                };
                await notificationService.SendNotificationAsync(pausedWatch, syntheticEvent, summary, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to send Layer 3 notification for watch {WatchId}", context.WatchId);
        }

        return new HealingResult
        {
            Outcome = HealingOutcome.RequiresUser,
            Message = $"Block '{context.BlockInstanceId}' has failed {context.ConsecutiveFailures} times. Automatic healing exhausted."
        };
    }

    private async Task<HealingResult> AttemptLayer1Async(HealingContext context, CancellationToken ct)
    {
        var html = context.CurrentHtml;

        if (string.IsNullOrEmpty(html))
        {
            var url = FindUrlFromPipeline(context.Pipeline);
            if (url is not null)
            {
                try
                {
                    var fetchResult = await contentFetcher.FetchAsync(url, new FetchOptions { UseJavaScript = true }, ct);
                    if (fetchResult.IsSuccess)
                        html = fetchResult.Html;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to fetch current HTML for healing");
                }
            }
        }

        if (string.IsNullOrEmpty(html))
        {
            return new HealingResult
            {
                Outcome = HealingOutcome.RequiresUser,
                Message = "Cannot fetch current page content for healing"
            };
        }

        var blockDef = context.Pipeline.Blocks.FirstOrDefault(b => b.Id == context.BlockInstanceId);
        if (blockDef is null)
        {
            return new HealingResult
            {
                Outcome = HealingOutcome.RequiresUser,
                Message = "Block not found in pipeline"
            };
        }

        var prompt = BuildLayer1Prompt(blockDef, context.ErrorMessage, html);

        try
        {
            var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions { ExpectJson = true }, ct);

            if (!response.IsSuccess || string.IsNullOrEmpty(response.Content))
            {
                return new HealingResult
                {
                    Outcome = HealingOutcome.RequiresUser,
                    Message = $"LLM failed to suggest fix: {response.ErrorMessage ?? "empty response"}"
                };
            }

            return ParseLayer1Response(response.Content, blockDef, context.Pipeline);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Layer 1 healing failed for block {BlockId}", context.BlockInstanceId);
            return new HealingResult
            {
                Outcome = HealingOutcome.RequiresUser,
                Message = $"LLM healing request failed: {ex.Message}"
            };
        }
    }

    private async Task<HealingResult> AttemptLayer2Async(HealingContext context, CancellationToken ct)
    {
        var currentHtml = context.CurrentHtml;
        var setupHtml = context.SetupTimeHtml;

        // Try to fetch current HTML if not provided
        if (string.IsNullOrEmpty(currentHtml))
        {
            var url = FindUrlFromPipeline(context.Pipeline);
            if (url is not null)
            {
                try
                {
                    var fetchResult = await contentFetcher.FetchAsync(url, new FetchOptions { UseJavaScript = true }, ct);
                    if (fetchResult.IsSuccess)
                        currentHtml = fetchResult.Html;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to fetch current HTML for Layer 2 diagnosis");
                }
            }
        }

        if (string.IsNullOrEmpty(currentHtml) || string.IsNullOrEmpty(setupHtml))
        {
            return new HealingResult
            {
                Outcome = HealingOutcome.RequiresUser,
                Message = "Cannot compare HTML snapshots — missing current or setup-time HTML"
            };
        }

        var blockDef = context.Pipeline.Blocks.FirstOrDefault(b => b.Id == context.BlockInstanceId);
        if (blockDef is null)
        {
            return new HealingResult
            {
                Outcome = HealingOutcome.RequiresUser,
                Message = "Block not found in pipeline"
            };
        }

        var prompt = BuildLayer2Prompt(blockDef, context.ErrorMessage, currentHtml, setupHtml);

        try
        {
            var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions { ExpectJson = true }, ct);

            if (!response.IsSuccess || string.IsNullOrEmpty(response.Content))
            {
                return new HealingResult
                {
                    Outcome = HealingOutcome.RequiresUser,
                    Message = $"LLM diagnosis failed: {response.ErrorMessage ?? "empty response"}"
                };
            }

            return ParseLayer2Response(response.Content, blockDef, context.Pipeline);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Layer 2 diagnosis failed for block {BlockId}", context.BlockInstanceId);
            return new HealingResult
            {
                Outcome = HealingOutcome.RequiresUser,
                Message = $"LLM diagnosis request failed: {ex.Message}"
            };
        }
    }

    private HealingResult ParseLayer1Response(string content, BlockDefinition blockDef, PipelineDefinition pipeline)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (root.TryGetProperty("newConfig", out var newConfig))
            {
                var updatedBlock = blockDef with { Config = newConfig.Clone() };
                var updatedBlocks = pipeline.Blocks
                    .Select(b => b.Id == blockDef.Id ? updatedBlock : b)
                    .ToList();
                var updatedPipeline = pipeline with { Blocks = updatedBlocks };

                return new HealingResult
                {
                    Outcome = HealingOutcome.Healed,
                    Message = "Block config updated by LLM",
                    UpdatedBlock = updatedBlock,
                    UpdatedPipeline = updatedPipeline
                };
            }
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Failed to parse Layer 1 LLM response as JSON");
        }

        return new HealingResult
        {
            Outcome = HealingOutcome.RequiresUser,
            Message = "LLM response did not contain valid fix"
        };
    }

    private HealingResult ParseLayer2Response(string content, BlockDefinition blockDef, PipelineDefinition pipeline)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (root.TryGetProperty("newConfig", out var newConfig))
            {
                var updatedBlock = blockDef with { Config = newConfig.Clone() };
                var updatedBlocks = pipeline.Blocks
                    .Select(b => b.Id == blockDef.Id ? updatedBlock : b)
                    .ToList();
                var updatedPipeline = pipeline with { Blocks = updatedBlocks };

                var diagnosis = root.TryGetProperty("diagnosis", out var diag) ? diag.GetString() : null;

                return new HealingResult
                {
                    Outcome = HealingOutcome.DiagnosedFixable,
                    Message = diagnosis ?? "Pipeline-level fix applied after HTML comparison",
                    UpdatedBlock = updatedBlock,
                    UpdatedPipeline = updatedPipeline
                };
            }

            // LLM diagnosed the problem but couldn't fix it
            if (root.TryGetProperty("diagnosis", out var diagOnly))
            {
                return new HealingResult
                {
                    Outcome = HealingOutcome.RequiresUser,
                    Message = diagOnly.GetString() ?? "LLM diagnosed the issue but could not suggest a fix"
                };
            }
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Failed to parse Layer 2 LLM response as JSON");
        }

        return new HealingResult
        {
            Outcome = HealingOutcome.RequiresUser,
            Message = "LLM diagnosis did not produce a valid fix"
        };
    }

    private static string BuildLayer1Prompt(BlockDefinition block, string error, string html)
    {
        var sanitizedHtml = Sanitize(html, "current_html");
        var sanitizedError = Sanitize(error, "error");

        var configJson = block.Config.HasValue
            ? block.Config.Value.GetRawText()
            : "{}";

        return $$"""
            A pipeline block of type '{{block.Type}}' (id: '{{block.Id}}') has failed with this error:
            {{sanitizedError}}

            The block's current configuration is:
            {{configJson}}

            Here is the current HTML of the page:
            {{sanitizedHtml}}

            The block used to work but now fails. The website likely changed its structure.
            Analyze the HTML and suggest an updated configuration that would make this block work again.

            Respond as JSON with this exact structure:
            {"newConfig": { ...updated block config with fixed selectors/values... } }

            Only include the newConfig property. The config should be a drop-in replacement
            for the current block config, keeping the same structure but with updated values
            (e.g., new CSS selectors, updated XPath expressions, etc.).
            """;
    }

    private static string BuildLayer2Prompt(BlockDefinition block, string error, string currentHtml, string setupHtml)
    {
        var sanitizedCurrent = Sanitize(currentHtml, "current_html");
        var sanitizedSetup = Sanitize(setupHtml, "setup_html");
        var sanitizedError = Sanitize(error, "error");

        var configJson = block.Config.HasValue
            ? block.Config.Value.GetRawText()
            : "{}";

        return $$"""
            A pipeline block of type '{{block.Type}}' (id: '{{block.Id}}') has been failing repeatedly.
            Error: {{sanitizedError}}

            Current block config:
            {{configJson}}

            The HTML when the watch was first set up looked like this:
            {{sanitizedSetup}}

            The HTML now looks like this:
            {{sanitizedCurrent}}

            Please diagnose what changed on the website and suggest an updated block configuration.

            Respond as JSON:
            {
              "diagnosis": "Brief explanation of what changed on the website",
              "newConfig": { ...updated block config... }
            }

            If you can identify the change but cannot suggest a fix, omit "newConfig" and only include "diagnosis".
            """;
    }

    internal static string? FindUrlFromPipeline(PipelineDefinition pipeline)
    {
        // Look for an Input or Navigate block that has a URL in its config
        foreach (var block in pipeline.Blocks)
        {
            if (block.Config is not { } config)
                continue;

            if (block.Type is not ("Input" or "Navigate"))
                continue;

            if (config.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
                return urlProp.GetString();
        }

        return null;
    }
}
