using System.Text.Json;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Core.Pipeline.Setup;

public interface IPipelineTemplateRegistry
{
    /// <summary>Get a pre-built pipeline template for a detected platform.</summary>
    PipelineTemplate? GetTemplate(string platformId, string intent);

    /// <summary>List all available templates.</summary>
    IReadOnlyList<PipelineTemplate> ListTemplates();
}

public record PipelineTemplate(string PlatformId, string Intent, string Description, PipelineDefinition Pipeline);

public class PipelineTemplateRegistry : IPipelineTemplateRegistry
{
    private readonly IReadOnlyList<PipelineTemplate> _templates =
    [
        CreateWorkdayTemplate(),
        CreateWordPressTemplate(),
        CreateGenericPriceTemplate()
    ];

    public PipelineTemplate? GetTemplate(string platformId, string intent)
    {
        var normalizedPlatformId = platformId?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedPlatformId))
            return null;

        var normalizedIntent = intent?.Trim().ToLowerInvariant() ?? string.Empty;

        var template = normalizedPlatformId switch
        {
            "workday" when IsJobIntent(normalizedIntent) => _templates.First(t => t.PlatformId == "workday"),
            "wordpress" => _templates.First(t => t.PlatformId == "wordpress"),
            "shopify" when IsPriceIntent(normalizedIntent) => _templates.First(t => t.PlatformId == "shopify"),
            _ => null
        };

        return template is null
            ? null
            : template with { Pipeline = ClonePipeline(template.Pipeline) };
    }

    public IReadOnlyList<PipelineTemplate> ListTemplates() => _templates;

    private static bool IsJobIntent(string intent) =>
        intent.Contains("job", StringComparison.Ordinal) ||
        intent.Contains("career", StringComparison.Ordinal) ||
        intent.Contains("hiring", StringComparison.Ordinal) ||
        intent.Contains("opening", StringComparison.Ordinal) ||
        intent.Contains("position", StringComparison.Ordinal);

    private static bool IsPriceIntent(string intent) =>
        intent.Contains("price", StringComparison.Ordinal) ||
        intent.Contains("cost", StringComparison.Ordinal) ||
        intent.Contains("discount", StringComparison.Ordinal) ||
        intent.Contains("sale", StringComparison.Ordinal);

    private static PipelineTemplate CreateWorkdayTemplate()
    {
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                Block("input-1", "Input", 0, new { url = "https://example.myworkdayjobs.com/en-US/careers" }),
                Block("navigate-1", "Navigate", 1, new { useJavaScript = false, timeout = 30000 }),
                Block("llmextract-1", "LlmExtract", 2, new
                {
                    prompt = "Extract the visible list of job openings as a JSON array. For each item include title, location, requisitionId if available, and applyUrl.",
                    outputSchema = new
                    {
                        type = "array",
                        items = new
                        {
                            title = "string",
                            location = "string",
                            requisitionId = "string",
                            applyUrl = "string"
                        }
                    }
                }),
                Block("listdiff-1", "ListDiff", 3, new { identityKey = "applyUrl", mode = "all_changes" }),
                Block("condition-1", "Condition", 4, new { field = "added.length", @operator = "greaterThan", value = 0 }),
                Block("notify-1", "Notify", 5, new { template = "New Workday job posting detected." }),
                Block("output-1", "Output", 6)
            ],
            Connections =
            [
                Connect("input-1", "url", "navigate-1", "url"),
                Connect("navigate-1", "html", "llmextract-1", "html"),
                Connect("llmextract-1", "data", "listdiff-1", "data"),
                Connect("listdiff-1", "result", "condition-1", "result"),
                Connect("condition-1", "signal", "notify-1", "signal"),
                Connect("llmextract-1", "data", "notify-1", "data"),
                Connect("llmextract-1", "data", "output-1", "data")
            ],
            Metadata = new PipelineMetadata
            {
                DisplayTitle = "Workday job board template",
                CreatedAt = DateTime.UtcNow,
                UserIntent = "Monitor Workday job openings",
                CardType = "jobs",
                EstimatedLlmCallsPerRun = 1
            }
        };

        return new PipelineTemplate(
            "workday",
            "jobs",
            "Workday job board — Navigate job board, extract visible openings, diff additions, then notify.",
            pipeline);
    }

    private static PipelineTemplate CreateWordPressTemplate()
    {
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                Block("input-1", "Input", 0, new { url = "https://example.com/feed/" }),
                Block("navigate-1", "Navigate", 1, new { useJavaScript = false, timeout = 30000 }),
                Block("llmextract-1", "LlmExtract", 2, new
                {
                    prompt = "Extract the RSS or WordPress feed items as a JSON array. For each item include title, link, publicationDate, and summary.",
                    outputSchema = new
                    {
                        type = "array",
                        items = new
                        {
                            title = "string",
                            link = "string",
                            publicationDate = "string",
                            summary = "string"
                        }
                    }
                }),
                Block("listdiff-1", "ListDiff", 3, new { identityKey = "link", mode = "additions_only" }),
                Block("condition-1", "Condition", 4, new { field = "added.length", @operator = "greaterThan", value = 0 }),
                Block("notify-1", "Notify", 5, new { template = "New WordPress post published." }),
                Block("output-1", "Output", 6)
            ],
            Connections =
            [
                Connect("input-1", "url", "navigate-1", "url"),
                Connect("navigate-1", "html", "llmextract-1", "html"),
                Connect("llmextract-1", "data", "listdiff-1", "data"),
                Connect("listdiff-1", "result", "condition-1", "result"),
                Connect("condition-1", "signal", "notify-1", "signal"),
                Connect("llmextract-1", "data", "notify-1", "data"),
                Connect("llmextract-1", "data", "output-1", "data")
            ],
            Metadata = new PipelineMetadata
            {
                DisplayTitle = "WordPress RSS template",
                CreatedAt = DateTime.UtcNow,
                UserIntent = "Monitor WordPress feed updates",
                CardType = "content",
                EstimatedLlmCallsPerRun = 1
            }
        };

        return new PipelineTemplate(
            "wordpress",
            "content",
            "WordPress blog/RSS — Navigate the feed, extract items, detect additions, then notify.",
            pipeline);
    }

    private static PipelineTemplate CreateGenericPriceTemplate()
    {
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                Block("input-1", "Input", 0, new { url = "https://example.com/products/widget" }),
                Block("navigate-1", "Navigate", 1, new
                {
                    useJavaScript = true,
                    timeout = 30000,
                    waitForSelector = ".price, .product__price, [data-product-price]"
                }),
                Block("extractschema-1", "ExtractSchema", 2, new
                {
                    scope = "body",
                    schema = new object[]
                    {
                        new { field = "price", selector = ".price, .product__price, [data-product-price]" },
                        new { field = "title", selector = "h1, .product-title, .product__title" }
                    }
                }),
                Block("numericdelta-1", "NumericDelta", 3, new { field = "price" }),
                Block("condition-1", "Condition", 4, new { field = "deltaPercent", @operator = "changedByPercent", value = 1 }),
                Block("notify-1", "Notify", 5, new { template = "Tracked price changed." }),
                Block("output-1", "Output", 6)
            ],
            Connections =
            [
                Connect("input-1", "url", "navigate-1", "url"),
                Connect("navigate-1", "html", "extractschema-1", "html"),
                Connect("extractschema-1", "data", "numericdelta-1", "data"),
                Connect("numericdelta-1", "result", "condition-1", "result"),
                Connect("condition-1", "signal", "notify-1", "signal"),
                Connect("extractschema-1", "data", "notify-1", "data"),
                Connect("extractschema-1", "data", "output-1", "data")
            ],
            Metadata = new PipelineMetadata
            {
                DisplayTitle = "Generic price tracking template",
                CreatedAt = DateTime.UtcNow,
                UserIntent = "Monitor product price changes",
                CardType = "price",
                EstimatedLlmCallsPerRun = 0
            }
        };

        return new PipelineTemplate(
            "shopify",
            "price",
            "Generic price tracking — Navigate, wait for price selector, extract price, compare deltas, then notify.",
            pipeline);
    }

    private static BlockDefinition Block(string id, string type, int position, object? config = null) =>
        new()
        {
            Id = id,
            Type = type,
            Position = position,
            Config = config is null ? null : JsonSerializer.SerializeToElement(config)
        };

    private static ConnectionDefinition Connect(string fromBlockId, string fromPort, string toBlockId, string toPort) =>
        new()
        {
            FromBlockId = fromBlockId,
            FromPort = fromPort,
            ToBlockId = toBlockId,
            ToPort = toPort
        };

    private static PipelineDefinition ClonePipeline(PipelineDefinition pipeline)
    {
        var json = JsonSerializer.Serialize(pipeline);
        return JsonSerializer.Deserialize<PipelineDefinition>(json)
            ?? throw new InvalidOperationException("Failed to clone pipeline template.");
    }
}
