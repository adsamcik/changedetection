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
└── ChangeDetection.Tests/        # TUnit tests with Shouldly
```

## Build & Test Commands

```powershell
# Build
dotnet build
```

### ⚠️ CRITICAL: Test Execution Rules

**ALWAYS use `./test.ps1`** - NEVER call `dotnet run` on tests directly!

```powershell
./test.ps1                              # All tests (uses cached responses)
./test.ps1 -Filter "*TestName*"         # Filter by name pattern
./test.ps1 -NoBuild                     # Skip build
./test.ps1 -TailLines 100               # Show more console output
./test.ps1 -IncludeOllama               # Populate LLM cache (requires Ollama)
./test.ps1 -IncludeInternet             # Populate content cache (requires internet)
./test.ps1 -IncludeOllama -IncludeInternet  # Full cache refresh
```

| Parameter | Description | Default |
|-----------|-------------|---------|
| `-Filter` | TUnit treenode filter | (all tests) |
| `-NoBuild` | Skip build step | `$false` |
| `-TailLines` | Console output lines | `50` |
| `-MaxParallel` | Max parallel tests | `8` |
| `-IncludeOllama` | LLM cache: CacheFirst mode | `$false` |
| `-IncludeInternet` | Content cache: CacheFirst mode | `$false` |

> **Why test.ps1?** It captures ALL output to `test-output.log` for debugging.
> Direct `dotnet run` loses critical diagnostic information including stack traces.
> 
> **After test failures:** Check `test-output.log` for full details.

### Test Caching Architecture

Tests use SQLite caches for **offline, deterministic execution**:

| Cache | Location | Purpose |
|-------|----------|---------|
| LLM Responses | `tests/.../Llm/Cache/llm-responses.db` | Cached Ollama/OpenAI responses |
| Content Fetch | `tests/.../Scraping/Cache/content-cache.db` | Cached external URL content |

**Default behavior (CacheOnly):**
- Tests return cached responses instantly
- Cache miss → test fails with clear error
- No Ollama or internet required

**With `-IncludeOllama`/`-IncludeInternet` (CacheFirst):**
- Cache hit → return cached response
- Cache miss → call real service, cache result
- Use to populate cache when dependencies available

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

### Terminal Command Output Handling

**NEVER filter long-running command output inline** (e.g., `| Select-Object -Last 30`)

Instead, always:
1. **Redirect output to a persistent file**
2. **Search, filter, and analyze from the file as needed**
3. **Keep the file until investigation is complete** - do NOT delete after first read

```powershell
# ❌ WRONG - output truncated, lost forever
dotnet build 2>&1 | Select-Object -Last 30

# ❌ WRONG - deleted immediately, can't search later
dotnet build 2>&1 | Out-File temp.log; Get-Content temp.log -Tail 30; Remove-Item temp.log

# ✅ CORRECT - capture to persistent file, analyze multiple times
dotnet build 2>&1 | Out-File build-output.log
Get-Content build-output.log -Tail 50                    # View end
Select-String -Path build-output.log -Pattern "error"   # Search for errors
Get-Content build-output.log | Select-Object -First 20  # View start
# Delete ONLY when done: Remove-Item build-output.log
```

> **Why?** Debugging often requires multiple searches and different views of output.
> Deleting immediately forces re-running commands, wasting time and potentially
> producing different results. Keep logs until the investigation is complete.

### Multi-Agent Awareness

When working on this codebase, be mindful that **other AI agents may be active**:
- **Check for recent changes** before making modifications to shared files
- **Avoid conflicting edits** - if a file was just modified, review those changes first
- **Don't assume stale context** - re-read files if significant time has passed
- **Coordinate on shared resources** - be cautious with files like `Program.cs`, shared services, or configuration

### Resource Efficiency

**Respect local compute resources and the user's time.**

1. **Optimize queries and searches** - Use targeted patterns instead of broad searches
2. **Minimize redundant operations** - Don't re-read files already in context
3. **Batch related operations** - Combine edits when possible
4. **Use appropriate tools** - `grep_search` for known patterns, `semantic_search` only when needed
5. **Limit scope** - Filter tests, builds, and searches to relevant subsets
6. **Avoid unnecessary rebuilds** - Use `-NoBuild` flags when binaries are current

| Anti-Pattern | Better Approach |
|--------------|-----------------|
| Running all tests for a single change | `./test.ps1 -Filter "*SpecificTest*"` |
| Multiple sequential small file reads | One larger read with context |
| Rebuilding after no code changes | Use `--no-build` or `-NoBuild` |
| Searching entire codebase for exact string | `grep_search` with `includePattern` |
| Reading files already shown in context | Use provided context directly |

## Specialized Instructions

Domain-specific guidance is in `.github/instructions/`:
- `testing.instructions.md` - **Test patterns, TestBase usage, test.ps1 script**
- `csharp.instructions.md` - C# 14 features, code style
- `blazor.instructions.md` - Blazor/SignalR streaming patterns
- `llm-pipeline.instructions.md` - LLM agent architecture
- `debugging.instructions.md` - **Investigation methodology** ← CRITICAL for bug fixes
- `implementation.instructions.md` - Quality standards

## Test Files Reference

| File | Purpose |
|------|---------|
| `test.ps1` | **REQUIRED** test runner script |
| `test-output.log` | Full test output (check after failures) |
| `TestResults/results.trx` | Structured XML results |
| `tests/ChangeDetection.Tests/TestBase.cs` | Base class for all tests |
| `tests/.../Llm/Cache/llm-responses.db` | SQLite cache for LLM responses |
| `tests/.../Scraping/Cache/content-cache.db` | SQLite cache for URL content |
| `tests/.../Llm/Cache/CachingWebApplicationFactory.cs` | Factory with dual caching |

