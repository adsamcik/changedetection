using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.LLM;
using ChangeDetection.Services.LLM.Factories;
using ChangeDetection.Tests.Infrastructure;
using ChangeDetection.Tests.Llm.Cache;
using ChangeDetection.Tests.Llm.TestHelpers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace ChangeDetection.Tests.EndToEnd.Scenarios;

/// <summary>
/// Test-specific extraction schema that supports LLM-friendly prompts.
/// Separate from the production ExtractionSchema to avoid conflicts.
/// </summary>
public class TestExtractionSchema
{
    /// <summary>Human-readable name for the schema.</summary>
    public required string Name { get; set; }

    /// <summary>Description of what is being extracted.</summary>
    public required string Description { get; set; }

    /// <summary>Fields to extract.</summary>
    public List<TestSchemaField> Fields { get; set; } = [];
}

/// <summary>
/// Test-specific schema field for LLM extraction.
/// </summary>
public class TestSchemaField
{
    /// <summary>Field name.</summary>
    public required string Name { get; set; }

    /// <summary>Data type as a string for LLM prompts.</summary>
    public required string Type { get; set; }

    /// <summary>Description of the field for LLM context.</summary>
    public required string Description { get; set; }
}

/// <summary>
/// Test helper service for structured data extraction using LLM.
/// </summary>
public class ObjectExtractionTestService(ILlmProviderChain llmProvider)
{
    public async Task<ExtractionResult> ExtractStructuredDataAsync(
        string html,
        TestExtractionSchema schema,
        CancellationToken ct = default)
    {
        var prompt = BuildExtractionPrompt(html, schema);
        var response = await llmProvider.ExecuteAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.1f,
            MaxTokens = 2000
        }, ct);

        if (!response.IsSuccess || string.IsNullOrEmpty(response.Content))
        {
            return new ExtractionResult { IsSuccess = false, Error = response.ErrorMessage ?? "LLM call failed" };
        }

        try
        {
            // Try to extract JSON from the response (handle markdown code blocks)
            var jsonContent = ExtractJsonFromResponse(response.Content);
            var jsonDoc = System.Text.Json.JsonDocument.Parse(jsonContent);
            var data = ConvertJsonElementToDict(jsonDoc.RootElement);
            return new ExtractionResult { IsSuccess = true, Data = data, RawResponse = response.Content };
        }
        catch (Exception ex)
        {
            return new ExtractionResult { IsSuccess = false, Error = $"JSON parse failed: {ex.Message}", RawResponse = response.Content };
        }
    }

    private static string ExtractJsonFromResponse(string response)
    {
        // Try to extract JSON from markdown code blocks
        var jsonMatch = System.Text.RegularExpressions.Regex.Match(
            response,
            @"```(?:json)?\s*\n?([\s\S]*?)\n?```",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        if (jsonMatch.Success)
        {
            return jsonMatch.Groups[1].Value.Trim();
        }

        // If no code block, assume the whole response is JSON
        return response.Trim();
    }

    private static Dictionary<string, object> ConvertJsonElementToDict(System.Text.Json.JsonElement element)
    {
        var result = new Dictionary<string, object>();

        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return result;
        }

        foreach (var prop in element.EnumerateObject())
        {
            result[prop.Name] = ConvertJsonElement(prop.Value);
        }

        return result;
    }

    private static object ConvertJsonElement(System.Text.Json.JsonElement element)
    {
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => element.GetString() ?? "",
            System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => "",
            System.Text.Json.JsonValueKind.Object => ConvertJsonElementToDict(element),
            System.Text.Json.JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            _ => element.GetRawText()
        };
    }

    private static string BuildExtractionPrompt(string html, TestExtractionSchema schema)
    {
        var fieldsDescription = string.Join("\n", schema.Fields.Select(f => $"- {f.Name} ({f.Type}): {f.Description}"));
        return $"""
            Extract structured data from the following HTML content.
            
            Schema: {schema.Name}
            Description: {schema.Description}
            
            Fields to extract:
            {fieldsDescription}
            
            Return ONLY valid JSON matching this schema. No explanation or markdown.
            
            HTML Content:
            {html}
            """;
    }
}

/// <summary>
/// Result of an extraction operation.
/// </summary>
public class ExtractionResult
{
    public bool IsSuccess { get; set; }
    public Dictionary<string, object> Data { get; set; } = [];
    public string? Error { get; set; }
    public string? RawResponse { get; set; }

    /// <summary>
    /// Gets a value from the extraction result, navigating nested structures.
    /// Supports paths like "items.0.name" to access nested arrays/objects.
    /// </summary>
    public string? GetString(string key)
    {
        // First try direct access
        if (Data.TryGetValue(key, out var directValue))
        {
            return ConvertToString(directValue);
        }

        // Try finding in any nested array (first item)
        foreach (var kv in Data)
        {
            if (kv.Value is List<object> list && list.Count > 0)
            {
                if (list[0] is Dictionary<string, object> firstItem)
                {
                    if (firstItem.TryGetValue(key, out var nestedValue))
                    {
                        return ConvertToString(nestedValue);
                    }
                }
            }
            else if (kv.Value is Dictionary<string, object> nested)
            {
                if (nested.TryGetValue(key, out var nestedValue))
                {
                    return ConvertToString(nestedValue);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets a list from the extraction result.
    /// </summary>
    public List<object>? GetList(string key)
    {
        // First try direct access
        if (Data.TryGetValue(key, out var directValue) && directValue is List<object> list)
        {
            return list;
        }

        // Try finding in any nested array (first item)
        foreach (var kv in Data)
        {
            if (kv.Value is List<object> outerList && outerList.Count > 0)
            {
                if (outerList[0] is Dictionary<string, object> firstItem)
                {
                    if (firstItem.TryGetValue(key, out var nestedValue) && nestedValue is List<object> nestedList)
                    {
                        return nestedList;
                    }
                }
            }
            else if (kv.Value is Dictionary<string, object> nested)
            {
                if (nested.TryGetValue(key, out var nestedValue) && nestedValue is List<object> nestedList)
                {
                    return nestedList;
                }
            }
        }

        return null;
    }

    private static string? ConvertToString(object? value)
    {
        return value switch
        {
            null => null,
            string s => s,
            _ => value.ToString()
        };
    }
}

/// <summary>
/// Base class for extraction tests providing common LLM provider setup.
/// </summary>
public abstract class ExtractionTestBase
{
    protected CachingHttpClientFactory? HttpClientFactory;

    /// <summary>
    /// Asserts extraction succeeded, or skips the test if failure is due to LLM cache miss in CacheOnly mode.
    /// </summary>
    protected static void AssertExtractionSuccessOrSkipOnCacheMiss(ExtractionResult result)
    {
        if (!result.IsSuccess && CacheSkipHelper.IsLlmCacheOnly)
        {
            Skip.Test($"LLM cache miss in CacheOnly mode: {result.Error}. Run with -IncludeOllama to populate cache.");
        }
        result.IsSuccess.ShouldBeTrue($"Extraction failed: {result.Error}");
    }

    protected async Task<ILlmProviderChain> CreateRealLlmProvider()
    {
        var providerRepo = new InMemoryRepository<Core.Entities.LlmProviderConfig>();
        var usageRepo = new InMemoryRepository<Core.Entities.LlmUsageRecord>();

        await providerRepo.InsertAsync(new Core.Entities.LlmProviderConfig
        {
            Id = Guid.NewGuid(),
            Name = "Ollama-Test",
            ProviderType = Core.Entities.LlmProviderType.Ollama,
            Model = "ministral-3:3b",
            Endpoint = "http://localhost:11434",
            Priority = 1,
            IsEnabled = true,
            IsHealthy = true,
            TimeoutSeconds = 60
        });

        var logger = Substitute.For<ILogger<LlmProviderChain>>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var llmLogService = Substitute.For<ILlmLogService>();

        var cacheMode = CachedLlmKernelFactory.GetDefaultCacheMode();
        HttpClientFactory = new CachingHttpClientFactory(cacheMode, Console.Out);
        TUnit.Core.TestContext.Current?.OutputWriter?.WriteLine($"=== LLM Cache Mode: {cacheMode} ===");

        IEnumerable<ILlmKernelFactory> factories = [
            new OllamaKernelFactory(),
            new OpenAIKernelFactory(),
            new AzureOpenAIKernelFactory(),
            new GeminiKernelFactory(),
            new ClaudeKernelFactory()
        ];

        return new LlmProviderChain(providerRepo, usageRepo, logger, serviceProvider, llmLogService, factories, HttpClientFactory);
    }
}
