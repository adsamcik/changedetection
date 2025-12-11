# Copilot Instructions for ChangeDetection

## Project Overview

.NET 10 Blazor application for monitoring website changes:
- **ASP.NET Core 10** with Blazor (Server and WebAssembly)
- **C# 14** with latest language features
- **LiteDB** for persistence
- **Playwright** for browser-based scraping
- **Semantic Kernel** for LLM integrations (OpenAI, Google Gemini)
- **DiffPlex** for diff generation
- **MailKit** for email notifications

## Solution Structure

```
src/
├── ChangeDetection/              # Main Blazor Server host
│   ├── Components/               # Blazor components
│   ├── Endpoints/                # Minimal API endpoints
│   ├── Hubs/                     # SignalR hubs
│   └── Services/                 # Server-side implementations
├── ChangeDetection.Client/       # Blazor WebAssembly client
├── ChangeDetection.Core/         # Domain entities and interfaces
└── ChangeDetection.Shared/       # Shared DTOs
tests/
└── ChangeDetection.Tests/        # xUnit tests with Shouldly
```

## C# 14 Features

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

## Code Style

1. **File-scoped namespaces**
2. **Primary constructors** for DI
3. **Records** for DTOs
4. **Collection expressions** `[]`
5. **Pattern matching**
6. **`required` modifier** for mandatory properties

### Service Pattern
```csharp
public class ServerWatchService(
    IRepository<WatchedSite> watchRepo,
    ILogger<ServerWatchService> logger) : IWatchService
{
    public async Task<WatchedSite?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        logger.LogDebug("Fetching watch {Id}", id);
        return await watchRepo.GetByIdAsync(id, ct);
    }
}
```

## Key Interfaces

- `IWatchService` - Watch CRUD and check operations
- `IRepository<T>` - Generic persistence abstraction
- `IContentFetcher` - HTTP/Playwright page fetching
- `IContentExtractor` - HTML to text extraction
- `IDiffService` - Content comparison
- `INotificationService` - Email/webhook delivery
- `ILlmProviderChain` - AI change summarization

## Testing

Use **xUnit** with **Shouldly** assertions.

## Watch Setup Pipeline

The watch setup pipeline is **LLM-only** with **no heuristics or regex fallbacks**. All input processing, URL extraction, content analysis, and selector generation must go through LLM agents.

### Architecture Principles

1. **No Heuristics** - Never use regex, pattern matching, or rule-based extraction. All understanding comes from LLM.
2. **No Fallbacks** - If LLM is unavailable, the operation fails gracefully with a clear error. No degraded modes.
3. **Multi-Agent Design** - Specialized agents handle focused tasks (URL extraction, content analysis, selector generation, validation, synthesis).
4. **Parallel Execution** - Independent agents run in parallel via `IAsyncEnumerable.Merge()`.
5. **Streaming Responses** - All agent outputs stream in real-time including intermediate reasoning.
6. **In-Memory Sessions** - Conversation state lives only in memory with 30-minute sliding expiration. No persistence.
7. **Input-Anchored Validation** - Only validation allowed is verifying extracted values exist in original input (URL not mangled, feedback matches presented options, no hallucinated values).

### Agent Types

| Agent | Responsibility |
|-------|----------------|
| `UrlExtractionAgent` | Extract URLs from natural language input |
| `ContentAnalysisAgent` | Analyze fetched page structure and user intent |
| `SelectorGenerationAgent` | Generate CSS/XPath selectors for target content |
| `ValidationAgent` | Verify extracted config against original input |
| `SynthesisAgent` | Combine agent outputs into final configuration |
| `ResolutionAgent` | Reconcile conflicting outputs between agents |

### Streaming Chunks

```csharp
public record AgentStreamChunk(
    string AgentName,
    ChunkType Type,      // Thinking, Intermediate, Question, Result, Validation
    string Content,
    float? Confidence,
    bool IsCollapsible); // Reasoning is collapsible, results are not
```

### UI Guidelines

- Reasoning is **collapsed by default** with "Show thinking" toggle
- Per-agent activity indicators show parallel work
- Final synthesized response displayed cleanly
- User can expand any agent's reasoning for transparency

## Blazor

- Use `@rendermode InteractiveServer` for server-side interactivity
- Use `NotFoundPage` parameter for 404 handling

## Debugging Methodology

### Complete Investigation Before Conclusions

**CRITICAL**: Never stop investigating when something "looks like the problem." Real bugs often involve:
- **Multiple contributing factors** - A "perfect storm" of conditions
- **Cascading failures** - One issue triggering another
- **Coincidental correlations** - Something suspicious that isn't actually the cause
- **Hidden dependencies** - Implicit coupling between components

### Investigation Workflow

1. **Create investigation todos FIRST** - List ALL areas to investigate before starting
2. **Complete ALL todos** - Even if an early finding looks like the answer
3. **Use neutral observation language** during evidence collection:
   - ✅ "Component X shows behavior Y"
   - ❌ "Component X is broken" or "Found the bug in X"
4. **NEVER short-circuit** - A "Synthesize findings" step must come AFTER all investigation
5. **Multi-factor analysis** - Always ask: "What else could contribute to this?"

### Evidence Collection Phase

During investigation, record observations without conclusions:
```
Observation 1: ContentFetcher returns null when URL has trailing slash
Observation 2: Repository query timeout set to 5s, average query takes 4.8s
Observation 3: SignalR hub disconnects after 30s idle
Observation 4: LLM provider rotates after 3 consecutive failures
```

### Synthesis Phase

Only after ALL observations, ask:
1. Which observations are **causally related**?
2. Which are **coincidental**?
3. Is there a **common root cause**?
4. Could multiple factors create a **"perfect storm"**?
5. What's the **minimal reproduction path**?

### Multi-Factor Problem Patterns

| Pattern | Description | Example |
|---------|-------------|---------|
| Perfect Storm | Multiple conditions align to cause failure | Slow network + aggressive timeout + retry exhaustion |
| Layered Failures | One issue masks/triggers another | Exception swallowed, then null propagates |
| Coincidental Timing | Unrelated events occur together | Deploy + traffic spike (traffic was the issue) |
| Hidden Coupling | Implicit dependencies between components | Shared static state, ambient context |

### Anti-Patterns to Avoid

| Anti-Pattern | Description |
|--------------|-------------|
| **Premature conclusion** | Stopping investigation when first issue found |
| **Confirmation bias** | Only looking for evidence supporting initial theory |
| **Single-cause assumption** | Believing bugs have exactly one cause |
| **Satisfaction trap** | Feeling "done" before complete analysis |
| **Echo chamber** | Re-reading same code expecting different insight |
| **Scope creep** | Investigating unrelated areas without evidence |

