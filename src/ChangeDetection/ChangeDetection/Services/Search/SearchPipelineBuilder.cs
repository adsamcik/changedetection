using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Search;

/// <summary>
/// Builds PipelineDefinition instances for search-based watches.
/// The generated pipeline: SearchBlock → (compare with previous via state store).
/// </summary>
public static class SearchPipelineBuilder
{
    /// <summary>
    /// Creates a pipeline definition that executes a search query.
    /// The SearchBlock outputs diffable text sorted by URL; the pipeline executor's
    /// state store handles comparison with the previous run.
    /// </summary>
    public static PipelineDefinition BuildSearchPipeline(SearchConfig config)
    {
        var searchConfig = new
        {
            query = config.Query,
            provider = config.ProviderId,
            category = config.Category,
            language = config.Language,
            timeRange = config.TimeRange,
            maxResults = config.MaxResults
        };

        return new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition
                {
                    Id = "search-1",
                    Type = "Search",
                    Config = JsonSerializer.SerializeToElement(searchConfig),
                    Position = 0
                },
                new BlockDefinition
                {
                    Id = "textdiff-1",
                    Type = "TextDiff",
                    Position = 1
                }
            ],
            Connections =
            [
                new ConnectionDefinition
                {
                    FromBlockId = "search-1",
                    FromPort = "text",
                    ToBlockId = "textdiff-1",
                    ToPort = "current"
                }
            ],
            Metadata = new PipelineMetadata
            {
                DisplayTitle = $"Search: {config.Query}",
                UserIntent = $"Monitor search results for '{config.Query}'",
                CreatedAt = DateTime.UtcNow
            }
        };
    }

    /// <summary>
    /// Serializes a search pipeline to JSON for storage on WatchedSite.PipelineDefinitionJson.
    /// </summary>
    public static string BuildSearchPipelineJson(SearchConfig config)
    {
        var pipeline = BuildSearchPipeline(config);
        return JsonSerializer.Serialize(pipeline, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
