---
applyTo: "**/*.cs"
---
# C# Code Style

## Language Version: C# 14

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

## Required Patterns

1. **File-scoped namespaces** - Always use single-line namespace declarations
2. **Primary constructors** for dependency injection
3. **Records** for DTOs and immutable data
4. **Collection expressions** `[]` instead of `new List<T>()`
5. **Pattern matching** for type checks and conditionals
6. **`required` modifier** for mandatory properties

## Service Pattern

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

## Architecture Rules

1. **Interface-First** - Define interfaces in `ChangeDetection.Core/Interfaces/` before implementing
2. **Implementation in Server** - Implement services in `ChangeDetection/Services/`
3. **Register in Program.cs** - Wire up with appropriate lifetime (Scoped/Singleton/Transient)
4. **Inject via primary constructor** - Never use `new` for dependencies
