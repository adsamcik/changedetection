using System.Text.Json;
using System.Text.Json.Serialization;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.LLM;
using ChangeDetection.Services.LLM.Factories;
using ChangeDetection.Tests.Llm.Cache;
using ChangeDetection.Tests.Llm.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ChangeDetection.Tests;

/// <summary>
/// Reads LLM provider configuration for tests.
/// 
/// Config layering (no hardcoded fallbacks):
///   1. test-llm-config.json        — committed default (Ollama)
///   2. test-llm-config.local.json  — gitignored local override (e.g. Copilot)
/// 
/// The local file completely replaces the base file when present.
/// Both files must be fully valid — there are no fallback defaults in code.
/// </summary>
public static class TestLlmConfig
{
    private const string BaseConfigFileName = "test-llm-config.json";
    private const string LocalConfigFileName = "test-llm-config.local.json";
    private static TestLlmSettings? _cached;

    public static TestLlmSettings Load()
    {
        if (_cached is not null)
            return _cached;

        // Local override takes priority over committed default
        var localPath = FindConfigFile(LocalConfigFileName);
        var basePath = FindConfigFile(BaseConfigFileName);

        var configPath = localPath ?? basePath;
        if (configPath is null)
            throw new InvalidOperationException(
                $"No test LLM config found. Expected '{BaseConfigFileName}' in the test project directory. " +
                "Run from the repository root or ensure the file is present.");

        var json = File.ReadAllText(configPath);
        _cached = JsonSerializer.Deserialize<TestLlmSettings>(json, JsonOptions)
                  ?? throw new InvalidOperationException($"Failed to deserialize {configPath}");

        _cached.Validate();
        return _cached;
    }

    /// <summary>
    /// Creates an LlmProviderConfig entity from the test settings.
    /// </summary>
    public static LlmProviderConfig ToProviderConfig(this TestLlmSettings settings) => new()
    {
        Id = Guid.NewGuid(),
        Name = settings.Name,
        ProviderType = settings.ProviderType,
        Endpoint = settings.Endpoint,
        Model = settings.Model,
        ApiKey = settings.ApiKey,
        IsEnabled = true,
        Priority = 1,
        TimeoutSeconds = settings.TimeoutSeconds,
        MaxRetries = settings.MaxRetries
    };

    /// <summary>
    /// Creates all kernel factories needed for test provider resolution.
    /// Includes all production factories so any configured provider type works.
    /// </summary>
    public static IEnumerable<ILlmKernelFactory> CreateAllFactories()
    {
        return
        [
            new OllamaKernelFactory(),
            new OpenAIKernelFactory(),
            new AzureOpenAIKernelFactory(),
            new GeminiKernelFactory(),
            new ClaudeKernelFactory(),
            new CopilotKernelFactory(
                NullLogger<CopilotKernelFactory>.Instance,
                NullLoggerFactory.Instance)
        ];
    }

    /// <summary>
    /// Creates a fully configured LlmProviderChain for tests.
    /// Provider type, model, and endpoint are all driven by config files.
    /// </summary>
    public static async Task<(LlmProviderChain Chain, CachingHttpClientFactory? HttpClientFactory)> CreateProviderChainAsync(
        TextWriter? output = null)
    {
        var settings = Load();
        var providerConfig = settings.ToProviderConfig();
        providerConfig.IsHealthy = true;

        var providerRepo = new InMemoryRepository<LlmProviderConfig>();
        var usageRepo = new InMemoryRepository<LlmUsageRecord>();
        await providerRepo.InsertAsync(providerConfig);

        var logger = Substitute.For<ILogger<LlmProviderChain>>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var llmLogService = Substitute.For<ILlmLogService>();

        var cacheMode = CachedLlmKernelFactory.GetDefaultCacheMode();
        var httpClientFactory = new CachingHttpClientFactory(cacheMode, output ?? Console.Out);

        output?.WriteLine($"=== LLM Provider: {settings.Name} ({settings.ProviderType}, model: {settings.Model}) ===");
        output?.WriteLine($"=== LLM Cache Mode: {cacheMode} ===");

        var chain = new LlmProviderChain(
            providerRepo, usageRepo, logger, serviceProvider,
            llmLogService, CreateAllFactories(), httpClientFactory);

        return (chain, httpClientFactory);
    }

    private static string? FindConfigFile(string fileName)
    {
        // Search from assembly location upward to find the config file
        var dir = Path.GetDirectoryName(typeof(TestLlmConfig).Assembly.Location);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
                return candidate;

            var testProjectDir = Path.Combine(dir, "tests", "ChangeDetection.Tests");
            candidate = Path.Combine(testProjectDir, fileName);
            if (File.Exists(candidate))
                return candidate;

            dir = Path.GetDirectoryName(dir);
        }

        var repoRoot = FindRepoRoot();
        if (repoRoot is not null)
        {
            var candidate = Path.Combine(repoRoot, "tests", "ChangeDetection.Tests", fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(TestLlmConfig).Assembly.Location);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

public class TestLlmSettings
{
    public required string Name { get; set; }
    public required LlmProviderType ProviderType { get; set; }
    public string? Endpoint { get; set; }
    public required string Model { get; set; }
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Validates that required fields are present for the configured provider type.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidOperationException("Test LLM config: 'name' is required.");
        if (string.IsNullOrWhiteSpace(Model))
            throw new InvalidOperationException("Test LLM config: 'model' is required.");

        switch (ProviderType)
        {
            case LlmProviderType.Ollama:
                if (string.IsNullOrWhiteSpace(Endpoint))
                    throw new InvalidOperationException("Test LLM config: 'endpoint' is required for Ollama provider.");
                break;
            case LlmProviderType.OpenAI:
            case LlmProviderType.Gemini:
            case LlmProviderType.Claude:
                if (string.IsNullOrWhiteSpace(ApiKey))
                    throw new InvalidOperationException($"Test LLM config: 'apiKey' is required for {ProviderType} provider.");
                break;
            case LlmProviderType.AzureOpenAI:
                if (string.IsNullOrWhiteSpace(ApiKey))
                    throw new InvalidOperationException("Test LLM config: 'apiKey' is required for AzureOpenAI provider.");
                if (string.IsNullOrWhiteSpace(Endpoint))
                    throw new InvalidOperationException("Test LLM config: 'endpoint' is required for AzureOpenAI provider.");
                break;
            case LlmProviderType.Copilot:
                // Copilot uses SDK — no endpoint or API key needed
                break;
        }
    }
}
