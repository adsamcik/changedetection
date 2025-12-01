# Copilot Instructions for ChangeDetection

## Project Overview

ChangeDetection is a .NET 10 Blazor application for monitoring website changes with AI-powered analysis:

- **ASP.NET Core 10** with Blazor (Server and WebAssembly hybrid)
- **C# 14** with latest language features
- **LiteDB** for embedded NoSQL persistence
- **Playwright** for JavaScript-rendered page scraping
- **Semantic Kernel** for LLM integrations (OpenAI, Google Gemini)
- **DiffPlex** for content diff generation
- **HtmlAgilityPack** for HTML parsing
- **MailKit** for email notifications
- **Polly** for resilience and retry policies

## Solution Structure

```
src/
├── ChangeDetection/              # Main Blazor Server host
│   └── ChangeDetection/
│       ├── Components/           # Blazor components (Pages, Layout)
│       ├── Endpoints/            # Minimal API endpoints
│       ├── Hubs/                 # SignalR hubs
│       └── Services/             # Server-side implementations
│           ├── Background/       # Background services
│           ├── Content/          # Content extraction
│           ├── LLM/              # LLM integration
│           ├── Notifications/    # Email/webhook notifications
│           ├── Persistence/      # LiteDB repositories
│           └── Scraping/         # Playwright fetching
├── ChangeDetection.Client/       # Blazor WebAssembly client
│   ├── Components/               # Client-side components
│   └── Pages/                    # Client-routable pages
├── ChangeDetection.Core/         # Domain layer (no dependencies)
│   ├── Entities/                 # Domain entities
│   └── Interfaces/               # Abstractions
└── ChangeDetection.Shared/       # Shared between server and client
    └── DTOs/                     # Data transfer objects
tests/
└── ChangeDetection.Tests/        # xUnit tests with Shouldly
```

## C# 14 Features to Use

### Field-Backed Properties
```csharp
public string Name
{
    get => field;
    set => field = value?.Trim() ?? throw new ArgumentNullException(nameof(value));
}
```

### Null-Conditional Assignment
```csharp
person?.Name = "Updated";  // Only assigns if person is not null
```

### Lambda Parameter Modifiers
```csharp
var increment = (ref int x) => x++;
```

### Unbound Generic Types in nameof
```csharp
var name = nameof(List<>);  // Returns "List"
```

## Code Style Guidelines

### General Conventions
1. **File-scoped namespaces** - Always use single-line namespace declarations
2. **Primary constructors** - Prefer for services with dependency injection
3. **Records** - Use for DTOs and immutable data structures
4. **Collection expressions** - Use `[]` syntax for collection initialization
5. **Pattern matching** - Use extensively for type checks and conditionals
6. **`required` modifier** - Use for mandatory properties on classes/records
7. **Nullable reference types** - Always enabled, handle nulls explicitly

### Naming Conventions
- Interfaces: `I` prefix (e.g., `IWatchService`, `IRepository<T>`)
- DTOs: Suffix with `Dto` (e.g., `WatchDetailDto`, `WatchCreateDto`)
- Endpoints: Static classes with `Endpoints` suffix
- Tests: Suffix with `Tests` (e.g., `ContentExtractorTests`)

### Entity Example
```csharp
namespace ChangeDetection.Core.Entities;

public class WatchedSite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Url { get; set; }
    public string? Name { get; set; }
    public string? CssSelector { get; set; }
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(30);
    public WatchStatus Status { get; set; } = WatchStatus.Active;
    public List<string> Tags { get; set; } = [];
}
```

### DTO Example
```csharp
namespace ChangeDetection.Shared.Dtos;

public class WatchCreateDto
{
    public required string Url { get; set; }
    public string? Title { get; set; }
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(1);
    public FetchSettingsDto FetchSettings { get; set; } = new();
}
```

### Service Example (Primary Constructor)
```csharp
namespace ChangeDetection.Services;

public class ServerWatchService(
    IRepository<WatchedSite> watchRepo,
    IRepository<ChangeSnapshot> snapshotRepo,
    IContentFetcher fetcher,
    ILogger<ServerWatchService> logger) : IWatchService
{
    public async Task<WatchedSite?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        logger.LogDebug("Fetching watch {Id}", id);
        return await watchRepo.GetByIdAsync(id, ct);
    }
}
```

### Minimal API Endpoints Example
```csharp
namespace ChangeDetection.Endpoints;

public static class WatchEndpoints
{
    public static RouteGroupBuilder MapWatchEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", GetAllWatches)
            .WithName("GetAllWatches")
            .Produces<List<WatchListItemDto>>();

        group.MapGet("/{id}", GetWatchById)
            .WithName("GetWatchById")
            .Produces<WatchDetailDto>()
            .Produces(404);

        return group;
    }

    private static async Task<IResult> GetWatchById(
        Guid id,
        IWatchService watchService,
        CancellationToken ct)
    {
        var watch = await watchService.GetByIdAsync(id, ct);
        return watch is null ? Results.NotFound() : Results.Ok(MapToDetailDto(watch));
    }
}
```

## Testing Conventions

Use **xUnit** with **Shouldly** assertions:

```csharp
namespace ChangeDetection.Tests.Content;

public class ContentExtractorTests
{
    private readonly ContentExtractor _sut = new();

    [Fact]
    public void ExtractText_WithSimpleHtml_ReturnsTextContent()
    {
        // Arrange
        var html = "<html><body><p>Hello World</p></body></html>";

        // Act
        var result = _sut.ExtractText(html);

        // Assert
        result.ShouldBe("Hello World");
    }

    [Theory]
    [InlineData("<p>Test</p>", "Test")]
    [InlineData("<div><span>Nested</span></div>", "Nested")]
    public void ExtractText_WithVariousHtml_ExtractsCorrectly(string html, string expected)
    {
        _sut.ExtractText($"<html><body>{html}</body></html>").ShouldBe(expected);
    }
}
```

## Blazor Conventions

### Component Structure
- Use `@page` directive for routable components
- Prefer `@rendermode InteractiveServer` for server-side interactivity
- Use `NotFoundPage` parameter for 404 handling in Routes.razor

### Form Handling
```razor
<EditForm Model="@model" OnValidSubmit="HandleSubmit">
    <DataAnnotationsValidator />
    <ValidationSummary />
    
    <InputText @bind-Value="model.Url" class="form-control" />
    <ValidationMessage For="@(() => model.Url)" />
    
    <button type="submit">Save</button>
</EditForm>
```

## Key Interfaces

- `IWatchService` - Watch CRUD and check operations
- `IRepository<T>` - Generic persistence abstraction
- `IContentFetcher` - HTTP/Playwright page fetching
- `IContentExtractor` - HTML to text extraction
- `IDiffService` - Content comparison
- `INotificationService` - Email/webhook delivery
- `ILlmProviderChain` - AI change summarization
- `IInputProcessor` - LLM input preprocessing

## Configuration

Settings are stored in `appsettings.json` and managed via `AppSettings` entity in LiteDB:
- LLM provider configurations (API keys, models)
- SMTP settings for email notifications
- Default check intervals
- Playwright browser options

## Common Tasks

### Adding a New Watch Feature
1. Add property to `WatchedSite` entity in Core
2. Add corresponding property to DTOs in Shared
3. Update `IWatchService` interface if needed
4. Implement in `ServerWatchService`
5. Add/update endpoint in `WatchEndpoints`
6. Update Blazor components

### Adding LLM Provider Support
1. Add configuration to `LlmProviderConfig` entity
2. Implement provider in `Services/LLM/`
3. Register in `LlmProviderChain`
4. Add DTOs for provider-specific settings
