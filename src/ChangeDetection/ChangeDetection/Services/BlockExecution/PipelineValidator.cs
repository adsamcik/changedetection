using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Validation;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.BlockExecution;

/// <summary>
/// Validates pipeline definitions for structural and semantic correctness.
/// Collects ALL errors and warnings in a single pass — no short-circuiting.
/// </summary>
public class PipelineValidator(ILogger<PipelineValidator> logger) : IPipelineValidator
{
    private static readonly HashSet<string> LlmBlockTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "LlmExtract", "LlmEvaluate", "LlmCraftPrompt"
    };
    private static readonly IEqualityComparer<(string BlockId, string PortName)> BlockPortComparer =
        new BlockPortKeyComparer();
    private static readonly IEqualityComparer<(string FromBlockId, string FromPort, string ToBlockId, string ToPort)> ConnectionComparer =
        new ConnectionKeyComparer();

    public ValidationResult Validate(PipelineDefinition definition, IBlockRegistry registry)
    {
        List<ValidationError> errors = [];
        List<ValidationWarning> warnings = [];

        var blockMap = BuildBlockMap(definition, errors);

        CheckInputBlock(definition, errors);
        CheckOutputBlock(definition, errors);
        CheckUnknownBlockTypes(definition, registry, errors);
        CheckSelfConnections(definition, errors);
        CheckConnectionEndpoints(definition, blockMap, registry, errors);
        CheckRequiredInputs(definition, registry, blockMap, errors);
        CheckConditionBlockInputs(definition, registry, blockMap, warnings);
        CheckDuplicateConnections(definition, errors);
        CheckMultipleConnectionsToSameInput(definition, warnings);
        CheckOrphanBlocks(definition, blockMap, errors);
        CheckCycles(definition, blockMap, errors);
        CheckLlmWarnings(definition, warnings);

        if (errors.Count > 0 || warnings.Count > 0)
        {
            logger.LogDebug("Pipeline validation: {ErrorCount} errors, {WarningCount} warnings",
                errors.Count, warnings.Count);
        }

        return new ValidationResult { Errors = errors, Warnings = warnings };
    }

    /// <summary>
    /// Builds a block ID → BlockDefinition map and detects duplicate IDs.
    /// </summary>
    private static Dictionary<string, BlockDefinition> BuildBlockMap(
        PipelineDefinition definition, List<ValidationError> errors)
    {
        var map = new Dictionary<string, BlockDefinition>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var block in definition.Blocks)
        {
            if (!seen.Add(block.Id))
            {
                errors.Add(new ValidationError("DUPLICATE_BLOCK_ID",
                    $"Duplicate block ID '{block.Id}'.", block.Id));
                continue;
            }

            map[block.Id] = block;
        }

        return map;
    }

    private static void CheckInputBlock(PipelineDefinition definition, List<ValidationError> errors)
    {
        var inputCount = definition.Blocks.Count(b =>
            string.Equals(b.Type, "Input", StringComparison.OrdinalIgnoreCase));

        if (inputCount == 0)
        {
            errors.Add(new ValidationError("MISSING_INPUT_BLOCK",
                "Pipeline must have exactly one block of type 'Input'."));
        }
        else if (inputCount > 1)
        {
            errors.Add(new ValidationError("MULTIPLE_INPUT_BLOCKS",
                $"Pipeline must have exactly one Input block, found {inputCount}."));
        }
    }

    private static void CheckOutputBlock(PipelineDefinition definition, List<ValidationError> errors)
    {
        var outputCount = definition.Blocks.Count(b =>
            string.Equals(b.Type, "Output", StringComparison.OrdinalIgnoreCase));

        if (outputCount == 0)
        {
            errors.Add(new ValidationError("MISSING_OUTPUT_BLOCK",
                "Pipeline must have exactly one block of type 'Output'."));
        }
        else if (outputCount > 1)
        {
            errors.Add(new ValidationError("MULTIPLE_OUTPUT_BLOCKS",
                $"Pipeline must have exactly one Output block, found {outputCount}."));
        }
    }

    private static void CheckUnknownBlockTypes(
        PipelineDefinition definition, IBlockRegistry registry, List<ValidationError> errors)
    {
        foreach (var block in definition.Blocks)
        {
            if (!registry.IsRegistered(block.Type))
            {
                errors.Add(new ValidationError("UNKNOWN_BLOCK_TYPE",
                    $"Block type '{block.Type}' is not registered.", block.Id));
            }
        }
    }

    private static void CheckSelfConnections(PipelineDefinition definition, List<ValidationError> errors)
    {
        foreach (var conn in definition.Connections)
        {
            if (string.Equals(conn.FromBlockId, conn.ToBlockId, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ValidationError("SELF_CONNECTION",
                    $"Block '{conn.FromBlockId}' cannot connect to itself.", conn.FromBlockId));
            }
        }
    }

    /// <summary>
    /// Validates connection endpoints: block existence, port existence, and port type compatibility.
    /// </summary>
    private static void CheckConnectionEndpoints(
        PipelineDefinition definition,
        Dictionary<string, BlockDefinition> blockMap,
        IBlockRegistry registry,
        List<ValidationError> errors)
    {
        foreach (var conn in definition.Connections)
        {
            var sourceValid = true;
            var targetValid = true;

            // Check source block exists
            if (!blockMap.TryGetValue(conn.FromBlockId, out var sourceBlock))
            {
                errors.Add(new ValidationError("INVALID_CONNECTION_SOURCE",
                    $"Connection references non-existent source block '{conn.FromBlockId}'."));
                sourceValid = false;
            }

            // Check target block exists
            if (!blockMap.TryGetValue(conn.ToBlockId, out var targetBlock))
            {
                errors.Add(new ValidationError("INVALID_CONNECTION_TARGET",
                    $"Connection references non-existent target block '{conn.ToBlockId}'."));
                targetValid = false;
            }

            // Only check ports if both blocks exist and are registered
            if (!sourceValid || !targetValid)
                continue;

            if (!registry.IsRegistered(sourceBlock!.Type) || !registry.IsRegistered(targetBlock!.Type))
                continue;

            var outputPorts = registry.GetOutputPorts(sourceBlock.Type);
            var sourcePort = outputPorts.FirstOrDefault(p =>
                string.Equals(p.Name, conn.FromPort, StringComparison.Ordinal));

            if (sourcePort is null)
            {
                errors.Add(new ValidationError("INVALID_PORT_NAME",
                    $"Output port '{conn.FromPort}' does not exist on block '{conn.FromBlockId}' (type '{sourceBlock.Type}').",
                    conn.FromBlockId));
                continue;
            }

            var inputPorts = registry.GetInputPorts(targetBlock.Type);
            var targetPort = inputPorts.FirstOrDefault(p =>
                string.Equals(p.Name, conn.ToPort, StringComparison.Ordinal));

            if (targetPort is null)
            {
                errors.Add(new ValidationError("INVALID_PORT_NAME",
                    $"Input port '{conn.ToPort}' does not exist on block '{conn.ToBlockId}' (type '{targetBlock.Type}').",
                    conn.ToBlockId));
                continue;
            }

            // Check port type compatibility
            if (!ArePortTypesCompatible(sourcePort.Type, targetPort.Type))
            {
                errors.Add(new ValidationError("PORT_TYPE_MISMATCH",
                    $"Port type mismatch: '{conn.FromBlockId}.{conn.FromPort}' ({sourcePort.Type}) → '{conn.ToBlockId}.{conn.ToPort}' ({targetPort.Type})."));
            }
        }
    }

    /// <summary>
    /// Checks whether two port types are compatible for connection.
    /// JSON-carrying types (PlainText, ExtractedObjects, DiffResult, SearchResults) are interchangeable.
    /// HtmlContent can flow into PlainText (HTML is text).
    /// </summary>
    private static bool ArePortTypesCompatible(PortType source, PortType target)
    {
        if (source == target) return true;

        // Configuration is compatible with everything (config injection)
        if (source == PortType.Configuration || target == PortType.Configuration)
            return true;

        // HtmlContent → PlainText (HTML is text)
        if (source == PortType.HtmlContent && target == PortType.PlainText)
            return true;

        // JSON-carrying types are interchangeable — blocks internally work with JsonElement
        // regardless of port type label. This allows template pipelines to work:
        //   HttpRequest.response (ExtractedObjects) → Paginate.json (PlainText)
        //   ListDiff.result (DiffResult) → Notify.data (ExtractedObjects)
        var jsonTypes = new HashSet<PortType>
        {
            PortType.PlainText,
            PortType.ExtractedObjects,
            PortType.DiffResult,
            PortType.SearchResults
        };

        if (jsonTypes.Contains(source) && jsonTypes.Contains(target))
            return true;

        return false;
    }

    /// <summary>
    /// Checks that all required input ports have at least one connection.
    /// </summary>
    private static void CheckRequiredInputs(
        PipelineDefinition definition,
        IBlockRegistry registry,
        Dictionary<string, BlockDefinition> blockMap,
        List<ValidationError> errors)
    {
        // Build set of (blockId, portName) that have incoming connections
        var connectedInputs = new HashSet<(string BlockId, string PortName)>(
            definition.Connections.Select(c => (c.ToBlockId, c.ToPort)),
            BlockPortComparer);

        foreach (var block in definition.Blocks)
        {
            if (!registry.IsRegistered(block.Type))
                continue;

            var inputPorts = registry.GetInputPorts(block.Type);

            foreach (var port in inputPorts)
            {
                if (port.Required && !connectedInputs.Contains((block.Id, port.Name)))
                {
                    errors.Add(new ValidationError("REQUIRED_INPUT_NOT_CONNECTED",
                        $"Required input port '{port.Name}' on block '{block.Id}' (type '{block.Type}') has no connection.",
                        block.Id));
                }
            }
        }
    }

    /// <summary>
    /// Condition blocks have all-optional inputs but should have at least one connected.
    /// </summary>
    private static void CheckConditionBlockInputs(
        PipelineDefinition definition,
        IBlockRegistry registry,
        Dictionary<string, BlockDefinition> blockMap,
        List<ValidationWarning> warnings)
    {
        var connectedInputs = new HashSet<(string BlockId, string PortName)>(
            definition.Connections.Select(c => (c.ToBlockId, c.ToPort)),
            BlockPortComparer);

        foreach (var block in definition.Blocks)
        {
            if (!string.Equals(block.Type, "Condition", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!registry.IsRegistered(block.Type))
                continue;

            var inputPorts = registry.GetInputPorts(block.Type);
            var hasAnyConnection = inputPorts.Any(p => connectedInputs.Contains((block.Id, p.Name)));

            if (!hasAnyConnection)
            {
                warnings.Add(new ValidationWarning("CONDITION_NO_INPUTS",
                    $"Condition block '{block.Id}' has no input connections. At least one input should be connected.",
                    block.Id));
            }
        }
    }

    /// <summary>
    /// Every block must be reachable from the Input block via BFS.
    /// </summary>
    private static void CheckOrphanBlocks(
        PipelineDefinition definition,
        Dictionary<string, BlockDefinition> blockMap,
        List<ValidationError> errors)
    {
        // Find Input block
        var inputBlock = definition.Blocks.FirstOrDefault(b =>
            string.Equals(b.Type, "Input", StringComparison.OrdinalIgnoreCase));

        if (inputBlock is null)
            return; // Already reported as MISSING_INPUT_BLOCK

        // Build adjacency list (forward edges from connections)
        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in definition.Blocks)
            adjacency[block.Id] = [];

        foreach (var conn in definition.Connections)
        {
            if (adjacency.ContainsKey(conn.FromBlockId))
                adjacency[conn.FromBlockId].Add(conn.ToBlockId);
        }

        // BFS from Input
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(inputBlock.Id);
        visited.Add(inputBlock.Id);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var neighbors))
                continue;

            foreach (var neighbor in neighbors)
            {
                if (visited.Add(neighbor))
                    queue.Enqueue(neighbor);
            }
        }

        // Any block not visited is orphaned
        foreach (var block in definition.Blocks)
        {
            if (!visited.Contains(block.Id))
            {
                errors.Add(new ValidationError("ORPHAN_BLOCK",
                    $"Block '{block.Id}' (type '{block.Type}') is not reachable from the Input block.",
                    block.Id));
            }
        }
    }

    /// <summary>
    /// Uses Kahn's algorithm (topological sort) to detect cycles.
    /// If not all blocks can be sorted, the remaining ones form a cycle.
    /// </summary>
    private static void CheckCycles(
        PipelineDefinition definition,
        Dictionary<string, BlockDefinition> blockMap,
        List<ValidationError> errors)
    {
        // Build in-degree map and adjacency list
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var block in definition.Blocks)
        {
            inDegree[block.Id] = 0;
            adjacency[block.Id] = [];
        }

        foreach (var conn in definition.Connections)
        {
            // Only count edges between existing blocks
            if (!inDegree.ContainsKey(conn.FromBlockId) || !inDegree.ContainsKey(conn.ToBlockId))
                continue;

            // Skip self-connections (already reported)
            if (string.Equals(conn.FromBlockId, conn.ToBlockId, StringComparison.OrdinalIgnoreCase))
                continue;

            adjacency[conn.FromBlockId].Add(conn.ToBlockId);
            inDegree[conn.ToBlockId]++;
        }

        // Kahn's algorithm
        var queue = new Queue<string>();
        foreach (var (blockId, degree) in inDegree)
        {
            if (degree == 0)
                queue.Enqueue(blockId);
        }

        var sortedCount = 0;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sortedCount++;

            foreach (var neighbor in adjacency[current])
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
            }
        }

        if (sortedCount < definition.Blocks.Count)
        {
            // Find blocks that are part of cycles (still have in-degree > 0)
            var cycleBlocks = inDegree
                .Where(kv => kv.Value > 0)
                .Select(kv => kv.Key)
                .ToList();

            errors.Add(new ValidationError("CYCLE_DETECTED",
                $"Pipeline contains a cycle involving blocks: {string.Join(", ", cycleBlocks)}."));
        }
    }

    private static void CheckDuplicateConnections(PipelineDefinition definition, List<ValidationError> errors)
    {
        var seen = new HashSet<(string FromBlockId, string FromPort, string ToBlockId, string ToPort)>(ConnectionComparer);
        foreach (var conn in definition.Connections)
        {
            if (!seen.Add((conn.FromBlockId, conn.FromPort, conn.ToBlockId, conn.ToPort)))
            {
                errors.Add(new ValidationError("DUPLICATE_CONNECTION",
                    $"Duplicate connection from {conn.FromBlockId}.{conn.FromPort} to {conn.ToBlockId}.{conn.ToPort}."));
            }
        }
    }

    private static void CheckMultipleConnectionsToSameInput(
        PipelineDefinition definition, List<ValidationWarning> warnings)
    {
        var targetCounts = new Dictionary<(string BlockId, string PortName), int>(BlockPortComparer);
        foreach (var conn in definition.Connections)
        {
            var key = (conn.ToBlockId, conn.ToPort);
            targetCounts.TryGetValue(key, out var count);
            targetCounts[key] = count + 1;
        }

        foreach (var ((blockId, port), count) in targetCounts)
        {
            if (count > 1)
            {
                warnings.Add(new ValidationWarning("MULTIPLE_CONNECTIONS_TO_INPUT",
                    $"Multiple connections to {blockId}.{port} \u2014 last value wins.", blockId));
            }
        }
    }

    private static void CheckLlmWarnings(PipelineDefinition definition, List<ValidationWarning> warnings)
    {
        var llmBlocks = definition.Blocks
            .Where(b => LlmBlockTypes.Contains(b.Type))
            .ToList();

        if (llmBlocks.Count > 0)
        {
            warnings.Add(new ValidationWarning("LLM_COST_WARNING",
                $"Pipeline contains {llmBlocks.Count} LLM block(s) ({string.Join(", ", llmBlocks.Select(b => b.Type))}). Each execution will incur LLM API costs."));
        }
    }

    private sealed class BlockPortKeyComparer : IEqualityComparer<(string BlockId, string PortName)>
    {
        public bool Equals((string BlockId, string PortName) x, (string BlockId, string PortName) y) =>
            string.Equals(x.BlockId, y.BlockId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.PortName, y.PortName, StringComparison.Ordinal);

        public int GetHashCode((string BlockId, string PortName) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.BlockId),
                StringComparer.Ordinal.GetHashCode(obj.PortName));
    }

    private sealed class ConnectionKeyComparer : IEqualityComparer<(string FromBlockId, string FromPort, string ToBlockId, string ToPort)>
    {
        public bool Equals(
            (string FromBlockId, string FromPort, string ToBlockId, string ToPort) x,
            (string FromBlockId, string FromPort, string ToBlockId, string ToPort) y) =>
            string.Equals(x.FromBlockId, y.FromBlockId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.FromPort, y.FromPort, StringComparison.Ordinal) &&
            string.Equals(x.ToBlockId, y.ToBlockId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.ToPort, y.ToPort, StringComparison.Ordinal);

        public int GetHashCode((string FromBlockId, string FromPort, string ToBlockId, string ToPort) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.FromBlockId),
                StringComparer.Ordinal.GetHashCode(obj.FromPort),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ToBlockId),
                StringComparer.Ordinal.GetHashCode(obj.ToPort));
    }
}
