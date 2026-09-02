using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Net;
using System.Threading.Channels;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Services.Pipeline;
using ChangeDetection.Services.Search;
using Microsoft.Extensions.Options;

namespace ChangeDetection.Services.GroupWatch;

public partial class GroupWatchDiscoveryService
{
    private async Task<PipelineDefinition?> BuildPipelineForPortalAsync(
        DiscoveredPortal portal,
        string userInput,
        StructuredDiscoveryIntent parsedIntent,
        IReadOnlyList<RelevanceKeyword> positiveKeywords,
        IReadOnlyList<RelevanceKeyword> negativeKeywords,
        CancellationToken ct)
    {
        var detectedPlatform = SetupFlowEnhancements.DetectPlatformFromUrl(portal.Url);
        if (!string.IsNullOrWhiteSpace(detectedPlatform))
        {
            var template = await setupFlowEnhancements.GetPlatformTemplateAsync(
                detectedPlatform,
                portal.Url,
                positiveKeywords,
                negativeKeywords,
                ct);

            if (template is not null)
            {
                logger.LogInformation(
                    "Using deterministic platform template {PlatformId} for {Url}",
                    detectedPlatform,
                    portal.Url);
                return CustomizeTemplatePipeline(template, portal, parsedIntent);
            }

            logger.LogInformation(
                "Detected platform {PlatformId} for {Url}, but no deterministic template is available — attempting LLM-based pipeline build",
                detectedPlatform,
                portal.Url);
        }
        else
        {
            logger.LogInformation(
                "Unknown platform for {Url} — attempting LLM-based pipeline build",
                portal.Url);
        }

        // Platforms without a deterministic template use the composable flow first,
        // then the generic listing pipeline if analysis is unavailable.

        try
        {
            var composablePipeline = await BuildPipelineWithComposableFlowAsync(
                portal, userInput, positiveKeywords, negativeKeywords, ct);

            logger.LogInformation(
                "LLM-based composable flow succeeded for {Url}",
                portal.Url);
            return CustomizeTemplatePipeline(composablePipeline, portal, parsedIntent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "LLM-based composable flow failed for {Url} — falling back to generic pipeline",
                portal.Url);
        }

        logger.LogInformation(
            "Using generic job listing pipeline for {Url}",
            portal.Url);
        return CreateGenericJobListingPipeline(portal.Url);
    }

    /// <summary>
    /// Creates a generic job listing pipeline for career pages without a known platform template.
    /// Uses broad CSS selectors to extract clickable links with titles from any page — no LLM needed.
    /// </summary>
    private static PipelineDefinition CreateGenericJobListingPipeline(string url)
    {
        var blocks = new List<BlockDefinition>
        {
            new()
            {
                Id = "input-1",
                Type = "Input",
                Position = 0,
                Config = JsonSerializer.SerializeToElement(new { url })
            },
            new()
            {
                Id = "navigate-1",
                Type = "Navigate",
                Position = 1,
                Config = JsonSerializer.SerializeToElement(new
                {
                    useJavaScript = true,
                    waitForSelector = "a[href]",
                    timeout = 30000
                })
            },
            new()
            {
                Id = "extractschema-1",
                Type = "ExtractSchema",
                Position = 2,
                Config = JsonSerializer.SerializeToElement(new
                {
                    scope = "main a[href], article a[href], .jobs a[href], ul a[href], li a[href]",
                    listMode = true,
                    schema = new object[]
                    {
                        new { field = "title", selector = "*" },
                        new { field = "url", selector = "a[href]" }
                    },
                    enableLlmFallback = false
                })
            },
            new()
            {
                Id = "listdiff-1",
                Type = "ListDiff",
                Position = 3,
                Config = JsonSerializer.SerializeToElement(new
                {
                    identityKey = "url",
                    mode = "all_changes"
                })
            },
            new()
            {
                Id = "output-1",
                Type = "Output",
                Position = 4
            }
        };

        var connections = new List<ConnectionDefinition>
        {
            new() { FromBlockId = "input-1", FromPort = "url", ToBlockId = "navigate-1", ToPort = "url" },
            new() { FromBlockId = "navigate-1", FromPort = "html", ToBlockId = "extractschema-1", ToPort = "html" },
            new() { FromBlockId = "extractschema-1", FromPort = "data", ToBlockId = "listdiff-1", ToPort = "data" },
            new() { FromBlockId = "listdiff-1", FromPort = "result", ToBlockId = "output-1", ToPort = "data" }
        };

        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

        return new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks = blocks,
            Connections = connections,
            Metadata = new PipelineMetadata
            {
                DisplayTitle = $"Monitor {host} job listings",
                CreatedAt = DateTime.UtcNow,
                UserIntent = "Generic job listing extraction (auto-generated for unknown platform)",
                EstimatedLlmCallsPerRun = 0,
                CardType = "list"
            }
        };
    }

    private async Task<UrlValidationResult> ValidatePortalUrlAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new UrlValidationResult(false, "Invalid URL", 0);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var client = httpClientFactory.CreateClient();
            using var response = await client.GetAsync(uri, timeoutCts.Token);
            var statusCode = (int)response.StatusCode;

            if (statusCode is >= 200 and < 400)
                return new UrlValidationResult(true, null, statusCode);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var responseBody = await ReadResponseBodyAsync(response, timeoutCts.Token);
                if (response.StatusCode == HttpStatusCode.Forbidden &&
                    ContainsCaptchaWall(responseBody))
                {
                    return new UrlValidationResult(false, "CAPTCHA wall detected", statusCode);
                }

                return new UrlValidationResult(true, "Login required", statusCode);
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return new UrlValidationResult(false, "URL is no longer available", statusCode);

            if (statusCode >= 500)
                return new UrlValidationResult(false, "Server error", statusCode);

            return new UrlValidationResult(false, $"Unexpected HTTP {statusCode}", statusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new UrlValidationResult(false, "Validation timed out", 0);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "HTTP error validating portal URL {PortalUrl}", url);
            return new UrlValidationResult(false, ex.Message, 0);
        }
    }

    private static async Task<string> ReadResponseBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool ContainsCaptchaWall(string responseBody) =>
        !string.IsNullOrWhiteSpace(responseBody) &&
        (responseBody.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
         responseBody.Contains("verify", StringComparison.OrdinalIgnoreCase));

    private sealed record PreparedPortalBuildResult(
        int Index,
        DiscoveredPortal Portal,
        UrlValidationResult Validation,
        PipelineDefinition? Pipeline,
        Exception? Error);

    private async Task<PipelineDefinition> BuildPipelineWithComposableFlowAsync(
        DiscoveredPortal portal,
        string userInput,
        IReadOnlyList<RelevanceKeyword> positiveKeywords,
        IReadOnlyList<RelevanceKeyword> negativeKeywords,
        CancellationToken ct)
    {
        var fullInput = $"{userInput} {portal.Url}";
        string? sessionId = null;
        string? lastMessage = null;

        await foreach (var progress in composableSetupPipeline.StartSetupAsync(
                           new SetupRequest { UserInput = fullInput }, ct))
        {
            lastMessage = progress.Error ?? progress.Message;

            if (progress.Type == SetupProgressType.Failed)
            {
                throw new InvalidOperationException(progress.Error ?? progress.Message);
            }

            if (progress.Type == SetupProgressType.CheckpointReached &&
                progress.Phase == SetupPhase.Checkpoint1 &&
                !string.IsNullOrWhiteSpace(progress.SessionId))
            {
                sessionId = progress.SessionId;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException(lastMessage ?? "Composable setup did not reach checkpoint 1.");
        }

        PipelineDefinition? pipeline = null;

        try
        {
            await foreach (var progress in composableSetupPipeline.ConfirmIntentAsync(sessionId, true, null, ct))
            {
                lastMessage = progress.Error ?? progress.Message;

                if (progress.Type == SetupProgressType.Failed)
                {
                    throw new InvalidOperationException(progress.Error ?? progress.Message);
                }

                if (progress.Type == SetupProgressType.CheckpointReached &&
                    progress.Phase == SetupPhase.Checkpoint2 &&
                    progress.Proposal?.Pipeline is not null)
                {
                    pipeline = ApplyRelevanceKeywords(progress.Proposal.Pipeline, positiveKeywords, negativeKeywords);
                    break;
                }
            }

            if (pipeline is null)
            {
                throw new InvalidOperationException(lastMessage ?? "Composable setup did not produce a pipeline proposal.");
            }

            return pipeline;
        }
        finally
        {
            try
            {
                await foreach (var _ in composableSetupPipeline.ConfirmPipelineAsync(
                                   sessionId,
                                   confirmed: false,
                                   feedback: "Automated group watch discovery captured the generated pipeline.",
                                   ct))
                {
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Cleanup of composable setup session {SessionId} failed", sessionId);
            }
        }
    }

    private async Task<Guid> CreateWatchForPortalAsync(
        DiscoveredPortal portal,
        string userInput,
        StructuredDiscoveryIntent parsedIntent,
        Guid groupId,
        PipelineDefinition? pipeline,
        CancellationToken ct)
    {
        var useJavaScript = pipeline is not null && DetectUseJavaScript(pipeline);
        var request = new CreateWatchRequest
        {
            Url = portal.Url,
            Name = portal.Title ?? portal.Domain,
            Description = portal.Reasoning,
            UserIntent = userInput,
            GroupId = groupId,
            CheckInterval = ParseFrequencyOrDefault(null, _options.DefaultCheckInterval),
            UseJavaScript = useJavaScript,
            Tags = BuildWatchTags(parsedIntent, portal),
            // Always skip initial check for group-created watches — the background service
            // will check them on the next cycle regardless of pipeline presence.
            SkipInitialCheck = true
        };

        var createdWatch = await watchService.CreateWatchAsync(request, ct);

        if (pipeline is not null)
        {
            createdWatch.PipelineDefinitionJson = PipelineSerializer.Serialize(pipeline);
        }
        // No template available — leave NeedsPipelineSetup=false so the background
        // service can attempt headless LLM pipeline building before falling back
        // to interactive setup.

        createdWatch.FetchSettings ??= new FetchSettings();
        createdWatch.FetchSettings.UseJavaScript = useJavaScript;

        await watchService.UpdateWatchAsync(createdWatch, ct);
        return createdWatch.Id;
    }

    private static PipelineDefinition CustomizeTemplatePipeline(
        PipelineDefinition pipeline,
        DiscoveredPortal portal,
        StructuredDiscoveryIntent parsedIntent)
    {
        var blocks = pipeline.Blocks
            .Select(block =>
            {
                if (block.Config is null)
                    return block;

                if (string.Equals(block.Type, "DataFilter", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(parsedIntent.Location))
                {
                    return block with
                    {
                        Config = ReplaceLocationConditions(block.Config.Value, parsedIntent.Location)
                    };
                }

                return block;
            })
            .ToList();

        return pipeline with
        {
            Blocks = blocks,
            Metadata = pipeline.Metadata is null
                ? new PipelineMetadata
                {
                    DisplayTitle = portal.Title ?? portal.Domain,
                    UserIntent = BuildIntentSummary(parsedIntent),
                    CreatedAt = DateTime.UtcNow
                }
                : pipeline.Metadata with
                {
                    DisplayTitle = pipeline.Metadata.DisplayTitle ?? portal.Title ?? portal.Domain,
                    UserIntent = pipeline.Metadata.UserIntent ?? BuildIntentSummary(parsedIntent)
                }
        };
    }

    private static PipelineDefinition ApplyRelevanceKeywords(
        PipelineDefinition pipeline,
        IReadOnlyList<RelevanceKeyword> positiveKeywords,
        IReadOnlyList<RelevanceKeyword> negativeKeywords)
        => PipelineKeywordPatcher.ApplyRelevanceKeywords(pipeline, positiveKeywords, negativeKeywords);

    private static JsonElement ReplaceLocationConditions(JsonElement existingConfig, string location)
    {
        var config = JsonNode.Parse(existingConfig.GetRawText()) as JsonObject ?? new JsonObject();

        // Determine the field name from the first existing condition (default to "locationsText")
        var fieldName = "locationsText";
        if (config["conditions"]?.AsArray() is { Count: > 0 } existing &&
            existing[0] is JsonObject first &&
            first["field"]?.GetValue<string>() is { } f)
        {
            fieldName = f;
        }

        // Build location terms: the raw location, its parts if comma-separated, and derived country
        var terms = DeriveLocationFilterTerms(location);

        var replacementConditions = new JsonArray();
        foreach (var term in terms)
        {
            replacementConditions.Add(new JsonObject
            {
                ["field"] = fieldName,
                ["operator"] = "contains",
                ["value"] = term
            });
        }

        config["conditions"] = replacementConditions;
        config["mode"] = "any";
        return JsonSerializer.SerializeToElement(config, JsonOptions);
    }

    /// <summary>
    /// Derives a set of location filter terms from a location string.
    /// E.g. "Copenhagen" → ["Copenhagen", "Denmark"], "Kastrup, Denmark" → ["Kastrup", "Denmark"].
    /// </summary>
    internal static List<string> DeriveLocationFilterTerms(string location)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Split comma-separated parts (e.g. "Kastrup, Denmark")
        var parts = location.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part))
                terms.Add(part);
        }

        // If there was no comma, add the raw location
        if (parts.Length <= 1)
            terms.Add(location.Trim());

        // Derive country from each part using known city → country mappings
        foreach (var part in parts)
        {
            if (CityToCountry.TryGetValue(part.Trim(), out var country))
                terms.Add(country);
        }

        // Also try the full location string as a city lookup
        if (CityToCountry.TryGetValue(location.Trim(), out var fullCountry))
            terms.Add(fullCountry);

        return terms.ToList();
    }

    /// <summary>
    /// Maps well-known biotech hub cities to their country names for location filtering.
    /// </summary>
    private static readonly Dictionary<string, string> CityToCountry = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Copenhagen"] = "Denmark", ["Kastrup"] = "Denmark", ["Lyngby"] = "Denmark",
        ["Bagsværd"] = "Denmark", ["Hillerød"] = "Denmark", ["Kalundborg"] = "Denmark",
        ["Munich"] = "Germany", ["Berlin"] = "Germany", ["Hamburg"] = "Germany",
        ["Frankfurt"] = "Germany", ["Heidelberg"] = "Germany", ["Tübingen"] = "Germany",
        ["Vienna"] = "Austria", ["Graz"] = "Austria",
        ["Amsterdam"] = "Netherlands", ["Leiden"] = "Netherlands", ["Rotterdam"] = "Netherlands",
        ["Brussels"] = "Belgium", ["Ghent"] = "Belgium", ["Mechelen"] = "Belgium",
        ["Basel"] = "Switzerland", ["Zurich"] = "Switzerland",
        ["Stockholm"] = "Sweden", ["Malmö"] = "Sweden", ["Lund"] = "Sweden", ["Gothenburg"] = "Sweden",
        ["Paris"] = "France", ["Lyon"] = "France", ["Strasbourg"] = "France",
        ["Helsinki"] = "Finland", ["Turku"] = "Finland",
        ["Oslo"] = "Norway", ["Bergen"] = "Norway",
        ["Prague"] = "Czech Republic", ["Brno"] = "Czech Republic",
        ["London"] = "United Kingdom", ["Cambridge"] = "United Kingdom", ["Oxford"] = "United Kingdom",
        ["Dublin"] = "Ireland",
        ["Luxembourg"] = "Luxembourg",
    };

    private static readonly Dictionary<string, string> CountryToAbbreviation = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Denmark"] = "DK",
        ["Germany"] = "DE",
        ["Austria"] = "AT",
        ["Netherlands"] = "NL",
        ["Belgium"] = "BE",
        ["Switzerland"] = "CH",
        ["Sweden"] = "SE",
        ["France"] = "FR",
        ["Finland"] = "FI",
        ["Norway"] = "NO",
        ["Czech Republic"] = "CZ",
        ["United Kingdom"] = "UK",
        ["Ireland"] = "IE",
        ["Luxembourg"] = "LU",
    };

}
