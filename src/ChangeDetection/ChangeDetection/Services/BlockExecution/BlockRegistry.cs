using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Acquisition;
using ChangeDetection.Services.Blocks.Advanced;
using ChangeDetection.Services.Blocks.Comparison;
using ChangeDetection.Services.Blocks.Decision;
using ChangeDetection.Services.Blocks.Extraction;
using ChangeDetection.Services.Blocks.Llm;
using ChangeDetection.Services.Blocks.Output;

namespace ChangeDetection.Services.BlockExecution;

/// <summary>
/// In-memory registry of block types with their port descriptors and factory functions.
/// </summary>
public class BlockRegistry : IBlockRegistry
{
    private readonly record struct BlockRegistration(
        IReadOnlyList<PortDescriptor> InputPorts,
        IReadOnlyList<PortDescriptor> OutputPorts,
        Func<IServiceProvider, IPipelineBlock>? Factory);

    private readonly Dictionary<string, BlockRegistration> _registrations = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRegistered(string blockType) => _registrations.ContainsKey(blockType);

    public IReadOnlyList<PortDescriptor> GetInputPorts(string blockType) =>
        _registrations.TryGetValue(blockType, out var reg)
            ? reg.InputPorts
            : throw new KeyNotFoundException($"Block type '{blockType}' is not registered.");

    public IReadOnlyList<PortDescriptor> GetOutputPorts(string blockType) =>
        _registrations.TryGetValue(blockType, out var reg)
            ? reg.OutputPorts
            : throw new KeyNotFoundException($"Block type '{blockType}' is not registered.");

    public IPipelineBlock CreateBlock(string blockType, IServiceProvider services)
    {
        if (!_registrations.TryGetValue(blockType, out var reg))
            throw new KeyNotFoundException($"Block type '{blockType}' is not registered.");

        return reg.Factory?.Invoke(services)
            ?? throw new InvalidOperationException($"Block type '{blockType}' has no factory registered.");
    }

    public IReadOnlyList<string> RegisteredBlockTypes => [.. _registrations.Keys];

    public void Register(
        string blockType,
        IReadOnlyList<PortDescriptor> inputPorts,
        IReadOnlyList<PortDescriptor> outputPorts,
        Func<IServiceProvider, IPipelineBlock>? factory = null)
    {
        _registrations[blockType] = new BlockRegistration(inputPorts, outputPorts, factory);
    }

    /// <summary>
    /// Registers all core block type port descriptors for validation.
    /// Factory functions are placeholders until blocks are implemented in 1.5-1.8.
    /// </summary>
    public static void RegisterCoreBlocks(BlockRegistry registry)
    {
        // Input: outputs url + config
        registry.Register("Input",
            inputPorts: [],
            outputPorts:
            [
                new PortDescriptor { Name = "url", Type = PortType.Url },
                new PortDescriptor { Name = "config", Type = PortType.Configuration }
            ],
            factory: _ => new InputBlock());

        // Output: accepts data
        registry.Register("Output",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [],
            factory: _ => new OutputBlock());

        // Navigate: url → page + html
        registry.Register("Navigate",
            inputPorts: [new PortDescriptor { Name = "url", Type = PortType.Url }],
            outputPorts:
            [
                new PortDescriptor { Name = "page", Type = PortType.PageReference },
                new PortDescriptor { Name = "html", Type = PortType.HtmlContent }
            ],
            factory: _ => new NavigateBlock());

        // Wait: page → page
        registry.Register("Wait",
            inputPorts: [new PortDescriptor { Name = "page", Type = PortType.PageReference }],
            outputPorts: [new PortDescriptor { Name = "page", Type = PortType.PageReference }],
            factory: _ => new WaitBlock());

        // Click: page → page
        registry.Register("Click",
            inputPorts: [new PortDescriptor { Name = "page", Type = PortType.PageReference }],
            outputPorts: [new PortDescriptor { Name = "page", Type = PortType.PageReference }],
            factory: _ => new ClickBlock());

        // Scroll: page → page
        registry.Register("Scroll",
            inputPorts: [new PortDescriptor { Name = "page", Type = PortType.PageReference }],
            outputPorts: [new PortDescriptor { Name = "page", Type = PortType.PageReference }],
            factory: _ => new ScrollBlock());

        // Filter: html → html
        registry.Register("Filter",
            inputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            outputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            factory: _ => new FilterBlock());

        // ExtractSchema: html → data
        registry.Register("ExtractSchema",
            inputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ => new ExtractSchemaBlock());

        // DataFilter: data → filtered data
        registry.Register("DataFilter",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [new PortDescriptor { Name = "filtered", Type = PortType.ExtractedObjects }],
            factory: _ => new DataFilterBlock());

        // HashCompare: data → result
        registry.Register("HashCompare",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [new PortDescriptor { Name = "result", Type = PortType.DiffResult }],
            factory: _ => new HashCompareBlock());

        // ListDiff: data → result
        registry.Register("ListDiff",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [new PortDescriptor { Name = "result", Type = PortType.DiffResult }],
            factory: _ => new ListDiffBlock());

        // StructDiff: data → result
        registry.Register("StructDiff",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [new PortDescriptor { Name = "result", Type = PortType.DiffResult }],
            factory: _ => new StructDiffBlock());

        // NumericDelta: data → result + value
        registry.Register("NumericDelta",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts:
            [
                new PortDescriptor { Name = "result", Type = PortType.DiffResult },
                new PortDescriptor { Name = "value", Type = PortType.NumericValue }
            ],
            factory: _ => new NumericDeltaBlock());

        // Condition: either data or result (both optional individually)
        registry.Register("Condition",
            inputPorts:
            [
                new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects, Required = false },
                new PortDescriptor { Name = "result", Type = PortType.DiffResult, Required = false }
            ],
            outputPorts: [new PortDescriptor { Name = "signal", Type = PortType.BooleanSignal }],
            factory: _ => new ConditionBlock());

        // Notify: signal + optional data → notification
        registry.Register("Notify",
            inputPorts:
            [
                new PortDescriptor { Name = "signal", Type = PortType.BooleanSignal },
                new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects, Required = false }
            ],
            outputPorts: [new PortDescriptor { Name = "notification", Type = PortType.Notification }],
            factory: _ => new NotifyBlock());

        // LLM blocks
        registry.Register("LlmExtract",
            inputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ => new LlmExtractBlock());

        registry.Register("LlmEvaluate",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [new PortDescriptor { Name = "result", Type = PortType.DiffResult }],
            factory: _ => new LlmEvaluateBlock());

        registry.Register("LlmCraftPrompt",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.PlainText }],
            factory: _ => new LlmCraftPromptBlock());

        // TextDiff: current + previous → result
        registry.Register("TextDiff",
            inputPorts:
            [
                new PortDescriptor { Name = "current", Type = PortType.PlainText },
                new PortDescriptor { Name = "previous", Type = PortType.PlainText }
            ],
            outputPorts: [new PortDescriptor { Name = "result", Type = PortType.DiffResult }],
            factory: _ => new TextDiffBlock());

        // LookupHistory: data → data (with history)
        registry.Register("LookupHistory",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ => new LookupHistoryBlock());

        // Transform: data → data (transformed)
        registry.Register("Transform",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ => new TransformBlock());

        // Aggregate: data → data (grouped)
        registry.Register("Aggregate",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ => new AggregateBlock());

        // Throttle: data → data (rate-limited)
        registry.Register("Throttle",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ => new ThrottleBlock());

        // Paginate: html → data (paginated content)
        registry.Register("Paginate",
            inputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ => new PaginateBlock());

        // Route: data → data (with _route field)
        registry.Register("Route",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ => new RouteBlock());

        // Enrich: data → data (enriched)
        registry.Register("Enrich",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ => new EnrichBlock());

        // Search: query → results + text
        registry.Register("Search",
            inputPorts: [new PortDescriptor { Name = "query", Type = PortType.PlainText, Description = "Search query string" }],
            outputPorts:
            [
                new PortDescriptor { Name = "results", Type = PortType.SearchResults, Description = "Structured search results" },
                new PortDescriptor { Name = "text", Type = PortType.PlainText, Description = "Diffable text representation" }
            ],
            factory: _ => new SearchBlock());
    }
}
