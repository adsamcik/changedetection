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

## Build & Test Commands

```powershell
# Build
dotnet build

# Run tests (capture output for analysis)
dotnet test --logger "console;verbosity=detailed" 2>&1 | Tee-Object -FilePath test-output.log

# Run specific tests
dotnet test --filter "FullyQualifiedName~TestName"
```

## Key Interfaces

| Interface | Purpose |
|-----------|---------|
| `IWatchService` | Watch CRUD and check operations |
| `IRepository<T>` | Generic persistence abstraction |
| `IContentFetcher` | HTTP/Playwright page fetching |
| `IContentExtractor` | HTML to text extraction |
| `IDiffService` | Content comparison |
| `INotificationService` | Email/webhook delivery |
| `ILlmProviderChain` | AI change summarization |

## Core Patterns

### Service Registration
1. Define interface in `ChangeDetection.Core/Interfaces/`
2. Implement in `ChangeDetection/Services/`
3. Register in `Program.cs`
4. Inject via primary constructor

### Code Style Essentials
- File-scoped namespaces
- Primary constructors for DI
- Records for DTOs
- Collection expressions `[]`
- `required` modifier for mandatory properties

## Critical Constraints

### LLM Pipeline: No Heuristics
The watch setup pipeline is **LLM-only**:
- Never use regex or pattern matching for content understanding
- If LLM unavailable, fail gracefully—no degraded modes
- All extraction/analysis through LLM agents

### Long-Running Operations
- Use `IAsyncEnumerable<T>` for streaming progress
- Never block waiting for completion
- Stream updates via SignalR

## Specialized Instructions

Domain-specific guidance is in `.github/instructions/`:
- `testing.instructions.md` - Test patterns, TestBase usage
- `csharp.instructions.md` - C# 14 features, code style
- `blazor.instructions.md` - Blazor/SignalR streaming patterns
- `llm-pipeline.instructions.md` - LLM agent architecture
- `debugging.instructions.md` - Investigation methodology
- `implementation.instructions.md` - Quality standards

