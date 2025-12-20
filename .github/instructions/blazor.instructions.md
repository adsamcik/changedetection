---
applyTo: "src/**/Components/**/*.razor,src/**/Hubs/**/*.cs,src/**/Pages/**/*.razor"
---
# Blazor & SignalR Patterns

## Render Modes

- Use `@rendermode InteractiveServer` for server-side interactivity
- Use `NotFoundPage` parameter for 404 handling

## SignalR Streaming Pattern

For long-running operations, stream progress to clients:

```csharp
// Interface in Core
public interface IMyPipeline
{
    IAsyncEnumerable<ProgressUpdate> ProcessAsync(Input input, CancellationToken ct);
}

// Hub streams to client
public async IAsyncEnumerable<ProgressDto> StreamOperation(
    Request request,
    [EnumeratorCancellation] CancellationToken ct)
{
    await foreach (var update in _pipeline.ProcessAsync(request.Input, ct))
    {
        yield return MapToDto(update);
    }
}
```

## Pipeline Stage Streaming

Multi-stage pipelines yield progress after each stage:

```csharp
public async IAsyncEnumerable<PipelineUpdate> ProcessAsync(
    string input,
    [EnumeratorCancellation] CancellationToken ct)
{
    yield return new PipelineUpdate(Stage.Started, "Processing request...");
    
    yield return new PipelineUpdate(Stage.UrlExtraction, "Extracting URL...");
    var url = await ExtractUrlAsync(input, ct);
    yield return new PipelineUpdate(Stage.UrlExtracted, $"Found URL: {url}");
    
    yield return new PipelineUpdate(Stage.ContentFetching, "Fetching content...");
    var content = await FetchContentAsync(url, ct);
    yield return new PipelineUpdate(Stage.ContentFetched, "Content retrieved");
}
```

## Critical Rules

- **Streaming Over Blocking** - Use `IAsyncEnumerable<T>` for long-running operations
- **Complete the Circuit** - Wire through all layers: Interface → Implementation → Hub → Client
