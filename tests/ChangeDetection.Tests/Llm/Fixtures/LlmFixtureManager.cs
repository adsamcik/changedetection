using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChangeDetection.Tests.Llm.Fixtures;

/// <summary>
/// Manages LLM response fixtures for deterministic testing.
/// Fixtures are stored as JSON files that can be captured from real Ollama responses
/// and replayed in tests without requiring a live LLM server.
/// </summary>
public static class LlmFixtureManager
{
    private static readonly string FixturesDirectory = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Llm", "Fixtures", "Responses");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Saves a captured HTTP exchange as a fixture file.
    /// </summary>
    /// <param name="name">Unique name for the fixture (e.g., "price-extraction-usd")</param>
    /// <param name="exchange">The captured HTTP exchange</param>
    /// <param name="description">Optional description of what this fixture tests</param>
    public static void SaveFixture(string name, CapturedExchange exchange, string? description = null)
    {
        EnsureDirectoryExists();

        var filePath = GetFixturePath(name);
        
        // Don't overwrite existing fixtures unless REFRESH_LLM_FIXTURES=true
        if (File.Exists(filePath) && 
            Environment.GetEnvironmentVariable("REFRESH_LLM_FIXTURES") != "true")
        {
            Console.WriteLine($"[LlmFixtureManager] Fixture already exists, skipping: {filePath}");
            Console.WriteLine($"[LlmFixtureManager] Set REFRESH_LLM_FIXTURES=true to overwrite existing fixtures");
            return;
        }

        var fixture = new LlmFixture
        {
            Name = name,
            Description = description,
            CapturedAt = DateTime.UtcNow,
            Request = new LlmFixtureRequest
            {
                Method = exchange.RequestMethod,
                Uri = exchange.RequestUri,
                Body = exchange.RequestBody
            },
            Response = new LlmFixtureResponse
            {
                StatusCode = exchange.ResponseStatusCode,
                Body = exchange.ResponseBody,
                Content = exchange.GetResponseContent()
            },
            DurationMs = exchange.DurationMs
        };

        var json = JsonSerializer.Serialize(fixture, JsonOptions);
        File.WriteAllText(filePath, json);

        Console.WriteLine($"[LlmFixtureManager] Saved fixture: {filePath}");
    }

    /// <summary>
    /// Loads a fixture by name.
    /// </summary>
    /// <param name="name">The fixture name (without .json extension)</param>
    /// <returns>The loaded fixture or null if not found</returns>
    public static LlmFixture? LoadFixture(string name)
    {
        var filePath = GetFixturePath(name);
        if (!File.Exists(filePath))
            return null;

        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<LlmFixture>(json, JsonOptions);
    }

    /// <summary>
    /// Gets all available fixture names.
    /// </summary>
    public static IEnumerable<string> GetAvailableFixtures()
    {
        EnsureDirectoryExists();
        return Directory.GetFiles(FixturesDirectory, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f));
    }

    /// <summary>
    /// Gets the response content from a fixture, ready to use with MockLlmHttpHandler.
    /// </summary>
    /// <param name="name">The fixture name</param>
    /// <returns>The LLM response content</returns>
    /// <exception cref="FileNotFoundException">If fixture doesn't exist</exception>
    public static string GetFixtureResponse(string name)
    {
        var fixture = LoadFixture(name)
            ?? throw new FileNotFoundException($"Fixture not found: {name}");

        return fixture.Response?.Content
            ?? throw new InvalidOperationException($"Fixture {name} has no response content");
    }

    /// <summary>
    /// Creates the full OpenAI-compatible response JSON from a fixture.
    /// Use this when you need the complete HTTP response body.
    /// </summary>
    public static string GetFixtureResponseBody(string name)
    {
        var fixture = LoadFixture(name)
            ?? throw new FileNotFoundException($"Fixture not found: {name}");

        return fixture.Response?.Body
            ?? throw new InvalidOperationException($"Fixture {name} has no response body");
    }

    private static string GetFixturePath(string name)
    {
        // Sanitize filename
        var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(FixturesDirectory, $"{safeName}.json");
    }

    private static void EnsureDirectoryExists()
    {
        if (!Directory.Exists(FixturesDirectory))
            Directory.CreateDirectory(FixturesDirectory);
    }
}

/// <summary>
/// Represents a stored LLM response fixture.
/// </summary>
public record LlmFixture
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public DateTime CapturedAt { get; init; }
    public LlmFixtureRequest? Request { get; init; }
    public LlmFixtureResponse? Response { get; init; }
    public long DurationMs { get; init; }
}

public record LlmFixtureRequest
{
    public string Method { get; init; } = "";
    public string Uri { get; init; } = "";
    public string? Body { get; init; }
}

public record LlmFixtureResponse
{
    public int StatusCode { get; init; }
    public string? Body { get; init; }
    /// <summary>
    /// The extracted assistant message content (convenience field).
    /// </summary>
    public string? Content { get; init; }
}
