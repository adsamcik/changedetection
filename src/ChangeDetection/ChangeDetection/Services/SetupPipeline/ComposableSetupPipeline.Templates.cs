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
    private bool TryGetTemplatePipeline(
        DetectedPlatform? detectedPlatform,
        ParsedIntent intent,
        out PipelineTemplate? selectedTemplate,
        out PipelineDefinition? pipeline)
    {
        selectedTemplate = null;
        pipeline = null;

        if (detectedPlatform is null || detectedPlatform.Confidence < TemplateConfidenceThreshold)
            return false;

        selectedTemplate = templateRegistry.GetTemplate(detectedPlatform.PlatformId, intent.Intent);
        if (selectedTemplate is null)
            return false;

        pipeline = selectedTemplate.Pipeline;
        return true;
    }

    private static ContentAnalysisResult BuildTemplateAnalysis(
        ParsedIntent intent,
        DetectedPlatform detectedPlatform,
        PipelineTemplate selectedTemplate)
    {
        var contentType = detectedPlatform.PlatformId switch
        {
            "workday" => "listing",
            "wordpress" => "feed",
            "shopify" => "product",
            _ => "other"
        };

        var selector = detectedPlatform.PlatformId switch
        {
            "shopify" => ".price, .product__price, [data-product-price]",
            _ => null
        };

        return new ContentAnalysisResult
        {
            ContentType = contentType,
            Regions = detectedPlatform.PlatformId switch
            {
                "workday" => ["job listings", "job metadata"],
                "wordpress" => ["post feed", "latest entries"],
                "shopify" => ["product price", "product title"],
                _ => []
            },
            HasPagination = detectedPlatform.PlatformId == "workday",
            NeedsJavaScript = detectedPlatform.PlatformId == "shopify",
            RecommendedSelector = selector,
            PageSummary = $"Detected {detectedPlatform.PlatformName} ({detectedPlatform.Confidence:0.00}) and selected the \"{selectedTemplate.Description}\" template for \"{intent.Intent}\"."
        };
    }

    private static PipelineDefinition SpecializeTemplatePipeline(
        PipelineDefinition template,
        ParsedIntent intent,
        ContentAnalysisResult analysis,
        DetectedPlatform detectedPlatform,
        PipelineTemplate selectedTemplate)
    {
        var sourceUrl = BuildTemplateSourceUrl(intent.Url, detectedPlatform.PlatformId);

        var blocks = template.Blocks.Select(block =>
        {
            if (!string.Equals(block.Type, "Input", StringComparison.OrdinalIgnoreCase))
                return block;

            return block with
            {
                Config = JsonSerializer.SerializeToElement(new
                {
                    url = sourceUrl,
                    intent = intent.Intent,
                    platform = detectedPlatform.PlatformId
                })
            };
        }).ToList();

        return template with
        {
            Blocks = blocks,
            Metadata = new PipelineMetadata
            {
                DisplayTitle = $"{intent.Summary ?? intent.Intent} ({detectedPlatform.PlatformName})",
                CreatedAt = DateTime.UtcNow,
                UserIntent = intent.Intent,
                EstimatedLlmCallsPerRun = template.Metadata?.EstimatedLlmCallsPerRun ?? 0,
                CardType = analysis.ContentType
            }
        };
    }

    private static string BuildTemplateSourceUrl(string url, string platformId)
    {
        if (!string.Equals(platformId, "wordpress", StringComparison.OrdinalIgnoreCase))
            return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        if (uri.AbsolutePath.EndsWith("/feed", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.EndsWith("/feed/", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Contains("feed=", StringComparison.OrdinalIgnoreCase))
            return url;

        var builder = new UriBuilder(uri);
        var path = builder.Path.TrimEnd('/');
        builder.Path = string.IsNullOrEmpty(path) ? "/feed/" : $"{path}/feed/";
        return builder.Uri.ToString();
    }

    private static ContentAnalysisResult BuildDeterministicAnalysis(
        ContentClassification classification, ParsedIntent intent)
    {
        return classification.Type switch
        {
            DetectedContentType.JobListing => new ContentAnalysisResult
            {
                ContentType = "listing",
                Regions = ["job listings", "job metadata"],
                HasPagination = classification.DetectedPlatform is "workday",
                NeedsJavaScript = false,
                PageSummary = BuildDeterministicSummary(classification)
            },
            DetectedContentType.ProductListing => new ContentAnalysisResult
            {
                ContentType = "product",
                Regions = ["product listings", "product details"],
                HasPagination = true,
                NeedsJavaScript = false,
                PageSummary = BuildDeterministicSummary(classification)
            },
            DetectedContentType.NewsFeed => new ContentAnalysisResult
            {
                ContentType = "article",
                Regions = ["news articles", "post entries"],
                HasPagination = false,
                NeedsJavaScript = false,
                PageSummary = BuildDeterministicSummary(classification)
            },
            DetectedContentType.ApiJson => new ContentAnalysisResult
            {
                ContentType = "other",
                Regions = ["API response data"],
                HasPagination = false,
                NeedsJavaScript = false,
                PageSummary = BuildDeterministicSummary(classification)
            },
            _ => new ContentAnalysisResult
            {
                ContentType = "other",
                Regions = [],
                HasPagination = false,
                NeedsJavaScript = classification.Type == DetectedContentType.JsShell,
                PageSummary = BuildDeterministicSummary(classification)
            }
        };
    }

    private static string BuildDeterministicSummary(ContentClassification classification)
        => $"Deterministic: {classification.Type} on {classification.DetectedPlatform ?? "unknown"} " +
           $"({classification.Confidence:P0}). Signals: {string.Join(", ", classification.Signals)}";

    private record MutationAnalysis
    {
        public List<MutationEntry> Mutations { get; init; } = [];
    }

    private record MutationEntry
    {
        public string Description { get; init; } = "";
        public List<string> PredictedFragileBlocks { get; init; } = [];
    }

    private static T DeserializeOrThrow<T>(string json, string typeName)
    {
        try
        {
            var result = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return result ?? throw new InvalidOperationException(
                $"Deserialization of {typeName} returned null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize {typeName} from LLM response: {ex.Message}\nContent: {json[..Math.Min(json.Length, 500)]}",
                ex);
        }
    }

    /// <summary>Removes expired sessions from the static dictionary.</summary>
    internal static void CleanupExpiredSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in Sessions)
        {
            if (now - kvp.Value.LastActivityAt > SessionExpiration)
                Sessions.TryRemove(kvp.Key, out _);
        }
    }

    /// <summary>In-memory state store for dry runs — no persistence needed.</summary>
    private sealed class InMemoryBlockStateStore : IBlockStateStore
    {
        private readonly ConcurrentDictionary<string, JsonElement> _store = new();

        public Task<JsonElement?> GetPreviousOutputAsync(string watchId, string blockInstanceId, CancellationToken ct = default)
        {
            var key = $"{watchId}:{blockInstanceId}";
            return Task.FromResult(_store.TryGetValue(key, out var value) ? value : (JsonElement?)null);
        }

        public Task<JsonElement?> GetCachedOutputAsync(
            string watchId,
            string blockInstanceId,
            string inputHash,
            string pipelineHash,
            CancellationToken ct = default)
        {
            return Task.FromResult((JsonElement?)null);
        }

        public Task SaveOutputAsync(
            string watchId,
            string blockInstanceId,
            JsonElement output,
            string? inputHash = null,
            string? pipelineHash = null,
            CancellationToken ct = default)
        {
            var key = $"{watchId}:{blockInstanceId}";
            _store[key] = output;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BlockExecutionSnapshot>> GetHistoryAsync(
            string watchId, string blockInstanceId, int maxResults = 10, CancellationToken ct = default)
        {
            return Task.FromResult<IReadOnlyList<BlockExecutionSnapshot>>([]);
        }
    }
}
