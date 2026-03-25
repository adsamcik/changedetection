using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.AutoHealing;
using ChangeDetection.Core.Pipeline.Validation;
using ChangeDetection.Services.AutoHealing;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.BlockExecution;

/// <summary>
/// Runtime engine that validates and executes a pipeline definition.
/// Handles topological ordering, port resolution, error propagation by criticality tier,
/// first-run baseline detection, and skip propagation.
/// </summary>
public class PipelineExecutor(
    IBlockRegistry registry,
    IPipelineValidator validator,
    IServiceProvider services,
    ILogger<PipelineExecutor> logger) : IPipelineExecutor
{
    private const int ExtractionMaxRetries = 2;
    private const int ExtractionRetryDelayMs = 500;

    public async Task<PipelineExecutionResult> ExecuteAsync(
        PipelineDefinition definition,
        Guid watchId,
        IBlockStateStore stateStore,
        object? page,
        CancellationToken ct = default,
        bool isDryRun = false)
    {
        var stopwatch = Stopwatch.StartNew();
        var blockResults = new Dictionary<string, BlockResult>(StringComparer.OrdinalIgnoreCase);
        var skippedBlockIds = new List<string>();

        logger.LogInformation("═══ Pipeline execution starting for watch {WatchId} with {BlockCount} blocks ═══", watchId, definition.Blocks.Count);

        // 1. Validate
        var validationResult = validator.Validate(definition, registry);
        if (!validationResult.IsValid)
        {
            stopwatch.Stop();
            var errorMsg = string.Join("; ", validationResult.Errors.Select(e => e.Message));
            logger.LogError("Pipeline validation failed: {Errors}", errorMsg);
            return new PipelineExecutionResult
            {
                Success = false,
                BlockResults = blockResults,
                Error = errorMsg,
                ExecutionDurationMs = stopwatch.ElapsedMilliseconds,
                WasBaseline = false,
                IsDegraded = false,
                SkippedBlockIds = skippedBlockIds
            };
        }

        // 2. Topological sort
        List<string> sortedBlockIds;
        try
        {
            sortedBlockIds = TopologicalSort(definition);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase))
        {
            stopwatch.Stop();
            logger.LogError("Pipeline contains a cycle: {Error}", ex.Message);
            return new PipelineExecutionResult
            {
                Success = false,
                BlockResults = blockResults,
                Error = ex.Message,
                ExecutionDurationMs = stopwatch.ElapsedMilliseconds,
                WasBaseline = false,
                IsDegraded = false,
                SkippedBlockIds = skippedBlockIds
            };
        }
        var pipelineHash = ComputePipelineSemanticHash(definition);

        // 3. Determine first run
        var isFirstRun = await IsFirstRunAsync(watchId, definition, stateStore, ct);

        // 4. Build adjacency structures for skip propagation
        var downstreamMap = BuildDownstreamMap(definition);
        var skippedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockOutputs = new Dictionary<string, JsonElement?>(StringComparer.OrdinalIgnoreCase);
        var isDegraded = false;
        var runTimestamp = DateTime.UtcNow;
        var loggerFactory = services.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        string? pipelineError = null;
        var aborted = false;

        // Cache watch's LLM budget to avoid per-block lookups
        var budgetCache = new BudgetCache();

        // Pre-index blocks for O(1) lookup in execution loop
        var blockIndex = definition.Blocks.ToDictionary(b => b.Id, StringComparer.OrdinalIgnoreCase);

        // Pipeline execution timeout (5 minutes)
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
        var linkedCt = timeoutCts.Token;

        // 5. Execute blocks in topological order
        foreach (var blockId in sortedBlockIds)
        {
            if (linkedCt.IsCancellationRequested)
            {
                logger.LogWarning("Pipeline execution cancelled at block '{BlockId}'", blockId);
                pipelineError = "Pipeline execution was cancelled.";
                aborted = true;
                break;
            }

            if (aborted)
                break;

            var blockDef = blockIndex[blockId];

            // Skip if this block is in the skipped set
            if (skippedSet.Contains(blockId))
            {
                var skipResult = BlockResult.Skip("Upstream block was skipped or failed");
                blockResults[blockId] = skipResult;
                skippedBlockIds.Add(blockId);
                logger.LogDebug("Skipping block '{BlockId}' — upstream dependency skipped/failed", blockId);
                continue;
            }

            // Resolve inputs from upstream connections
            var inputs = ResolveInputs(blockId, definition, blockOutputs);

            // Create per-block logger
            var blockLogger = loggerFactory?.CreateLogger($"Pipeline.Block.{blockDef.Type}.{blockId}")
                ?? logger;

            var context = new BlockContext
            {
                WatchId = watchId,
                RunTimestamp = runTimestamp,
                BlockInstanceId = blockId,
                Inputs = inputs,
                CancellationToken = linkedCt,
                Logger = blockLogger,
                StateStore = stateStore,
                Page = page,
                Services = services,
                IsFirstRun = isFirstRun,
                PipelineDefinition = definition,
                AllBlockOutputs = blockOutputs
            };

            // Create and execute the block
            IPipelineBlock block;
            try
            {
                block = registry.CreateBlock(blockDef.Type, services);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create block '{BlockId}' of type '{BlockType}'", blockId, blockDef.Type);
                var failResult = BlockResult.Failed($"Failed to create block: {ex.Message}");
                blockResults[blockId] = failResult;
                pipelineError = $"Block '{blockId}' creation failed: {ex.Message}";
                MarkDownstreamSkipped(blockId, downstreamMap, skippedSet);
                aborted = true;
                break;
            }

            string? inputHash = null;
            if (block.IsCacheable)
            {
                var computedInputHash = ComputeInputHash(inputs);
                inputHash = computedInputHash;
                if (!context.IsFirstRun)
                {
                    var cached = await stateStore.GetCachedOutputAsync(
                        watchId.ToString(), blockId, computedInputHash, pipelineHash, linkedCt);

                    if (cached.HasValue)
                    {
                        var cachedOutput = IsComparisonBlock(block)
                            ? CreateSyntheticUnchangedComparisonOutput(cached.Value)
                            : cached.Value;

                        logger.LogInformation(
                            "↺ Cache hit for block '{BlockId}' (type: {BlockType})",
                            blockId, blockDef.Type);

                        var cachedResult = BlockResult.CachedResult(cachedOutput);
                        blockResults[blockId] = cachedResult;
                        blockOutputs[blockId] = cachedOutput;
                        await stateStore.SaveOutputAsync(
                            watchId.ToString(),
                            blockId,
                            cachedOutput,
                            inputHash,
                            pipelineHash,
                            linkedCt);
                        continue;
                    }
                }
            }

            // Check LLM budget before executing LLM blocks
            if (IsLlmBlock(blockDef.Type))
            {
                var (exceeded, currentCost) = await CheckLlmBudgetAsync(
                    watchId, budgetCache, linkedCt);

                if (exceeded)
                {
                    logger.LogWarning(
                        "LLM budget exceeded for watch {WatchId} (current: {Cost:C}, budget: {Budget:C}). Skipping block '{BlockId}'.",
                        watchId, currentCost, budgetCache.MonthlyBudget, blockId);
                    var budgetSkip = BlockResult.Skip("LLM budget exceeded for this watch");
                    blockResults[blockId] = budgetSkip;
                    skippedBlockIds.Add(blockId);
                    isDegraded = true;
                    MarkDownstreamSkipped(blockId, downstreamMap, skippedSet);
                    continue;
                }
            }

            logger.LogInformation("▶ Executing block '{BlockId}' (type: {BlockType})", blockId, blockDef.Type);
            var result = await ExecuteBlockWithErrorHandling(block, context, blockId, downstreamMap, skippedSet);
            blockResults[blockId] = result;
            logger.LogInformation("✓ Block '{BlockId}' completed: Success={Success}, HasOutput={HasOutput}", blockId, result.Success, result.Output is not null);

            if (result.Output.HasValue)
            {
                blockOutputs[blockId] = result.Output;
                await stateStore.SaveOutputAsync(
                    watchId.ToString(),
                    blockId,
                    result.Output.Value,
                    inputHash,
                    pipelineHash,
                    linkedCt);
            }
            else
            {
                blockOutputs[blockId] = null;
            }

            // Record LLM cost after successful execution
            if (IsLlmBlock(blockDef.Type) && result.Success)
            {
                await RecordLlmCostAsync(watchId, blockId, result, services, linkedCt);
            }

            // Handle result by criticality
            if (!result.Success)
            {
                // Attempt auto-healing for failed blocks
                await TryAutoHealAsync(watchId, blockId, blockDef.Type,
                    result.Error ?? "Unknown error", definition, linkedCt);

                switch (block.CriticalityTier)
                {
                    case BlockCriticalityTier.Infrastructure:
                    case BlockCriticalityTier.Acquisition:
                        logger.LogError("{Tier} block '{BlockId}' failed: {Error}. Aborting pipeline.",
                            block.CriticalityTier, blockId, result.Error);
                        pipelineError = $"{block.CriticalityTier} block '{blockId}' failed: {result.Error}";
                        MarkDownstreamSkipped(blockId, downstreamMap, skippedSet);
                        aborted = true;
                        break;

                    case BlockCriticalityTier.Extraction:
                        logger.LogError("Extraction block '{BlockId}' failed after retries: {Error}. Aborting pipeline.",
                            blockId, result.Error);
                        pipelineError = $"Extraction block '{blockId}' failed: {result.Error}";
                        MarkDownstreamSkipped(blockId, downstreamMap, skippedSet);
                        aborted = true;
                        break;

                    case BlockCriticalityTier.Analysis:
                        logger.LogWarning("Analysis block '{BlockId}' failed: {Error}. Skipping downstream.",
                            blockId, result.Error);
                        blockResults[blockId] = BlockResult.Skip($"Analysis block failed: {result.Error}");
                        skippedBlockIds.Add(blockId);
                        skippedSet.Add(blockId);
                        isDegraded = true;
                        MarkDownstreamSkipped(blockId, downstreamMap, skippedSet);
                        break;

                    case BlockCriticalityTier.Delivery:
                        logger.LogWarning("Delivery block '{BlockId}' failed: {Error}. Continuing (outbox retry).",
                            blockId, result.Error);
                        skippedBlockIds.Add(blockId);
                        break;
                }
            }
            else if (result.Status == BlockExecutionStatus.Skipped)
            {
                skippedBlockIds.Add(blockId);
            }

            // Condition block with BooleanSignal=false: skip all downstream
            if (result.Success && result.Output.HasValue &&
                string.Equals(blockDef.Type, "Condition", StringComparison.OrdinalIgnoreCase))
            {
                if (IsBooleanSignalFalse(result.Output.Value))
                {
                    logger.LogDebug("Condition block '{BlockId}' returned false — skipping downstream blocks", blockId);
                    MarkDownstreamSkipped(blockId, downstreamMap, skippedSet);
                }
            }
        }

        stopwatch.Stop();

        // 6. Find Output block result
        var outputBlock = definition.Blocks.FirstOrDefault(b =>
            string.Equals(b.Type, "Output", StringComparison.OrdinalIgnoreCase));
        JsonElement? outputData = null;
        if (outputBlock is not null && blockResults.TryGetValue(outputBlock.Id, out var outputResult))
        {
            outputData = outputResult.Output;
        }

        logger.LogInformation("═══ Pipeline execution complete for watch {WatchId}: {ResultCount} block results ═══", watchId, blockResults.Count);

        return new PipelineExecutionResult
        {
            Success = !aborted,
            BlockResults = blockResults,
            OutputData = outputData,
            Error = pipelineError,
            ExecutionDurationMs = stopwatch.ElapsedMilliseconds,
            WasBaseline = isFirstRun,
            IsDegraded = isDegraded,
            SkippedBlockIds = skippedBlockIds
        };
    }

    private async Task<BlockResult> ExecuteBlockWithErrorHandling(
        IPipelineBlock block,
        BlockContext context,
        string blockId,
        Dictionary<string, HashSet<string>> downstreamMap,
        HashSet<string> skippedSet)
    {
        if (block.CriticalityTier == BlockCriticalityTier.Extraction)
        {
            return await ExecuteWithRetries(block, context, blockId, ExtractionMaxRetries);
        }

        try
        {
            return await block.ExecuteAsync(context);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Block '{BlockId}' threw an exception", blockId);
            return BlockResult.Failed($"Unhandled exception: {ex.Message}");
        }
    }

    private async Task<BlockResult> ExecuteWithRetries(
        IPipelineBlock block,
        BlockContext context,
        string blockId,
        int maxRetries)
    {
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await block.ExecuteAsync(context);
                if (result.Success)
                    return result;

                if (attempt < maxRetries)
                {
                    logger.LogWarning(
                        "Extraction block '{BlockId}' failed (attempt {Attempt}/{Max}): {Error}. Retrying...",
                        blockId, attempt + 1, maxRetries + 1, result.Error);
                    await Task.Delay(ExtractionRetryDelayMs, context.CancellationToken);
                }
                else
                {
                    return result;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt < maxRetries)
                {
                    logger.LogWarning(ex,
                        "Extraction block '{BlockId}' threw exception (attempt {Attempt}/{Max}). Retrying...",
                        blockId, attempt + 1, maxRetries + 1);
                    await Task.Delay(ExtractionRetryDelayMs, context.CancellationToken);
                }
                else
                {
                    return BlockResult.Failed($"Unhandled exception after {maxRetries + 1} attempts: {ex.Message}");
                }
            }
        }

        // Should not reach here, but safety net
        return BlockResult.Failed("Exhausted retries");
    }

    /// <summary>
    /// Topological sort via Kahn's algorithm. Returns block IDs in execution order.
    /// </summary>
    private static List<string> TopologicalSort(PipelineDefinition definition)
    {
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var block in definition.Blocks)
        {
            inDegree[block.Id] = 0;
            adjacency[block.Id] = [];
        }

        foreach (var conn in definition.Connections)
        {
            if (!inDegree.ContainsKey(conn.FromBlockId) || !inDegree.ContainsKey(conn.ToBlockId))
                continue;
            if (string.Equals(conn.FromBlockId, conn.ToBlockId, StringComparison.OrdinalIgnoreCase))
                continue;

            adjacency[conn.FromBlockId].Add(conn.ToBlockId);
            inDegree[conn.ToBlockId]++;
        }

        var queue = new Queue<string>();
        foreach (var (blockId, degree) in inDegree)
        {
            if (degree == 0)
                queue.Enqueue(blockId);
        }

        var sorted = new List<string>();
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted.Add(current);

            foreach (var neighbor in adjacency[current])
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
            }
        }

        if (sorted.Count < definition.Blocks.Count)
            throw new InvalidOperationException(
                $"Pipeline contains a cycle — topological sort produced {sorted.Count} of {definition.Blocks.Count} blocks.");

        return sorted;
    }

    /// <summary>
    /// Checks if any block has previous output in the state store, indicating this is not a first run.
    /// </summary>
    private static async Task<bool> IsFirstRunAsync(
        Guid watchId,
        PipelineDefinition definition,
        IBlockStateStore stateStore,
        CancellationToken ct)
    {
        var watchIdStr = watchId.ToString();
        foreach (var block in definition.Blocks)
        {
            var previous = await stateStore.GetPreviousOutputAsync(watchIdStr, block.Id, ct);
            if (previous.HasValue)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Builds a map from each block ID to all direct downstream block IDs.
    /// </summary>
    private static Dictionary<string, HashSet<string>> BuildDownstreamMap(PipelineDefinition definition)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in definition.Blocks)
            map[block.Id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var conn in definition.Connections)
        {
            if (map.TryGetValue(conn.FromBlockId, out var set))
                set.Add(conn.ToBlockId);
        }

        return map;
    }

    /// <summary>
    /// Marks all blocks transitively downstream of the given block as skipped.
    /// </summary>
    private static void MarkDownstreamSkipped(
        string blockId,
        Dictionary<string, HashSet<string>> downstreamMap,
        HashSet<string> skippedSet)
    {
        var queue = new Queue<string>();
        if (downstreamMap.TryGetValue(blockId, out var directDownstream))
        {
            foreach (var ds in directDownstream)
                queue.Enqueue(ds);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!skippedSet.Add(current))
                continue;

            if (downstreamMap.TryGetValue(current, out var next))
            {
                foreach (var n in next)
                    queue.Enqueue(n);
            }
        }
    }

    /// <summary>
    /// Resolves input port values for a block from upstream block outputs.
    /// </summary>
    private static Dictionary<string, JsonElement> ResolveInputs(
        string blockId,
        PipelineDefinition definition,
        Dictionary<string, JsonElement?> blockOutputs)
    {
        var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var conn in definition.Connections)
        {
            if (!string.Equals(conn.ToBlockId, blockId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (blockOutputs.TryGetValue(conn.FromBlockId, out var output) && output.HasValue)
            {
                // Extract the specific port's value from the block output.
                // Multi-output blocks return a JSON object keyed by port name;
                // single-output blocks may return the value directly.
                var resolved = output.Value;
                if (!string.IsNullOrEmpty(conn.FromPort) &&
                    resolved.ValueKind == JsonValueKind.Object &&
                    resolved.TryGetProperty(conn.FromPort, out var portValue))
                {
                    resolved = portValue;
                }

                inputs[conn.ToPort] = resolved;
            }
        }

        return inputs;
    }

    private static string ComputeInputHash(IReadOnlyDictionary<string, JsonElement> resolvedInputs)
    {
        var semanticInputs = resolvedInputs
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => new
            {
                kvp.Key,
                Value = kvp.Value
            })
            .ToArray();

        var json = JsonSerializer.Serialize(semanticInputs);
        return ComputeSha256Hash(json);
    }

    private static string ComputePipelineSemanticHash(PipelineDefinition definition)
    {
        var semanticDefinition = new
        {
            definition.SchemaVersion,
            Blocks = definition.Blocks.Select(block => new
            {
                block.Id,
                block.Type,
                block.Config
            }).ToArray(),
            Connections = definition.Connections.ToArray()
        };

        var json = JsonSerializer.Serialize(semanticDefinition);
        return ComputeSha256Hash(json);
    }

    private static string ComputeSha256Hash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    private static bool IsComparisonBlock(IPipelineBlock block) =>
        block.OutputPorts.Any(port => port.Type == PortType.DiffResult);

    private static JsonElement CreateSyntheticUnchangedComparisonOutput(JsonElement cachedOutput)
    {
        if (cachedOutput.ValueKind != JsonValueKind.Object)
            return JsonSerializer.SerializeToElement(new { changed = false });

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in cachedOutput.EnumerateObject())
        {
            values[property.Name] = property.Value.Clone();
        }

        values["changed"] = JsonSerializer.SerializeToElement(false);
        return JsonSerializer.SerializeToElement(values);
    }

    /// <summary>
    /// Checks if a Condition block's output indicates BooleanSignal=false.
    /// </summary>
    private static bool IsBooleanSignalFalse(JsonElement output)
    {
        // Check for direct boolean false
        if (output.ValueKind == JsonValueKind.False)
            return true;

        // Check for object with a "signal" or "value" property that is false
        if (output.ValueKind == JsonValueKind.Object)
        {
            if (output.TryGetProperty("signal", out var signal) && signal.ValueKind == JsonValueKind.False)
                return true;
            if (output.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.False)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the block type is an LLM block (LlmExtract, LlmEvaluate, LlmCraftPrompt).
    /// </summary>
    private static bool IsLlmBlock(string blockType) =>
        string.Equals(blockType, "LlmExtract", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(blockType, "LlmEvaluate", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(blockType, "LlmCraftPrompt", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Records a block failure and attempts auto-healing if thresholds are exceeded.
    /// Auto-healing results are logged but do not alter the current run — healed config
    /// takes effect on the next execution.
    /// </summary>
    private async Task TryAutoHealAsync(Guid watchId, string blockInstanceId, string blockType,
        string errorMessage, PipelineDefinition pipeline, CancellationToken ct)
    {
        try
        {
            var failureTracker = services.GetService(typeof(IFailureTracker)) as IFailureTracker;
            if (failureTracker is null) return;

            var failureCount = await failureTracker.RecordFailureAsync(watchId, blockInstanceId, errorMessage, ct);

            var healingService = services.GetService(typeof(IAutoHealingService)) as IAutoHealingService;
            if (healingService is null) return;

            // Look up setup-time HTML for Layer 2 diagnosis
            string? setupTimeHtml = null;
            var watchRepo = services.GetService(typeof(IRepository<WatchedSite>)) as IRepository<WatchedSite>;
            if (watchRepo is not null)
            {
                var watch = await watchRepo.GetByIdAsync(watchId, ct);
                setupTimeHtml = watch?.SetupTimeHtml;
            }

            var context = new HealingContext
            {
                WatchId = watchId,
                BlockInstanceId = blockInstanceId,
                BlockType = blockType,
                ErrorMessage = errorMessage,
                ConsecutiveFailures = failureCount,
                Pipeline = pipeline,
                SetupTimeHtml = setupTimeHtml,
                Services = services
            };

            var result = await healingService.AttemptHealAsync(context, ct);

            switch (result.Outcome)
            {
                case HealingOutcome.Healed:
                    logger.LogInformation(
                        "Auto-healing succeeded for block '{BlockId}' on watch {WatchId}: {Message}. Fix applies on next run.",
                        blockInstanceId, watchId, result.Message);
                    if (result.UpdatedPipeline is not null)
                    {
                        // Persist the healed pipeline for next run (reuse watchRepo from above)
                        if (watchRepo is not null)
                        {
                            var healedWatch = await watchRepo.GetByIdAsync(watchId, ct);
                            if (healedWatch is not null)
                            {
                                // Store pre-healing config for potential rollback
                                logger.LogInformation("Storing pre-healing pipeline for watch {WatchId} for potential rollback: {OldPipeline}",
                                    watchId, healedWatch.PipelineDefinitionJson);
                                healedWatch.PipelineDefinitionJson = PipelineSerializer.Serialize(result.UpdatedPipeline);
                                await watchRepo.UpdateAsync(healedWatch, ct);
                            }
                        }
                    }
                    await failureTracker.ResetFailuresAsync(watchId, blockInstanceId, ct);
                    break;

                case HealingOutcome.DiagnosedFixable:
                    // Layer 2 diagnosed a fix — persist it same as Layer 1 heal
                    logger.LogInformation(
                        "Auto-healing (Layer 2) diagnosed fix for block '{BlockId}' on watch {WatchId}: {Message}. Fix applies on next run.",
                        blockInstanceId, watchId, result.Message);
                    if (result.UpdatedPipeline is not null)
                    {
                        if (watchRepo is not null)
                        {
                            var diagWatch = await watchRepo.GetByIdAsync(watchId, ct);
                            if (diagWatch is not null)
                            {
                                // Store pre-healing config for potential rollback
                                logger.LogInformation("Storing pre-healing pipeline for watch {WatchId} for potential rollback: {OldPipeline}",
                                    watchId, diagWatch.PipelineDefinitionJson);
                                diagWatch.PipelineDefinitionJson = PipelineSerializer.Serialize(result.UpdatedPipeline);
                                await watchRepo.UpdateAsync(diagWatch, ct);
                            }
                        }
                    }
                    await failureTracker.ResetFailuresAsync(watchId, blockInstanceId, ct);
                    break;

                case HealingOutcome.RequiresUser:
                    logger.LogWarning(
                        "Auto-healing exhausted for block '{BlockId}' on watch {WatchId}: {Message}",
                        blockInstanceId, watchId, result.Message);
                    break;

                default:
                    logger.LogDebug("Auto-healing for block '{BlockId}': {Outcome} — {Message}",
                        blockInstanceId, result.Outcome, result.Message);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Auto-healing attempt failed for block '{BlockId}'", blockInstanceId);
        }
    }

    /// <summary>
    /// Checks if the watch's LLM budget has been exceeded. Caches the budget on first call.
    /// </summary>
    private async Task<(bool Exceeded, decimal CurrentCost)> CheckLlmBudgetAsync(
        Guid watchId, BudgetCache cache, CancellationToken ct)
    {
        if (!cache.Loaded)
        {
            cache.Loaded = true;
            var watchRepo = services.GetService(typeof(IRepository<WatchedSite>)) as IRepository<WatchedSite>;
            if (watchRepo is not null)
            {
                var watch = await watchRepo.GetByIdAsync(watchId, ct);
                cache.MonthlyBudget = watch?.MonthlyLlmBudget;
            }
        }

        if (cache.MonthlyBudget is null)
            return (false, 0);

        var costTracker = services.GetService(typeof(ILlmCostTracker)) as ILlmCostTracker;
        if (costTracker is null)
            return (false, 0);

        var exceeded = await costTracker.IsBudgetExceededAsync(watchId, cache.MonthlyBudget.Value, ct);
        var currentCost = exceeded ? await costTracker.GetCurrentMonthCostAsync(watchId, ct) : 0;
        return (exceeded, currentCost);
    }

    /// <summary>
    /// Records LLM cost for a block execution, extracting token counts from the block result when available.
    /// </summary>
    private async Task RecordLlmCostAsync(Guid watchId, string blockInstanceId,
        BlockResult blockResult, IServiceProvider sp, CancellationToken ct)
    {
        try
        {
            var costTracker = sp.GetService(typeof(ILlmCostTracker)) as ILlmCostTracker;
            if (costTracker is null) return;

            // Try to extract token counts from block result output
            int inputTokens = 0, outputTokens = 0;
            decimal estimatedCost = 0.01m; // fallback
            string modelName = "unknown";

            if (blockResult.Output.HasValue && blockResult.Output.Value.ValueKind == JsonValueKind.Object)
            {
                var output = blockResult.Output.Value;
                if (output.TryGetProperty("inputTokens", out var inTok) && inTok.ValueKind == JsonValueKind.Number)
                    inputTokens = inTok.GetInt32();
                if (output.TryGetProperty("outputTokens", out var outTok) && outTok.ValueKind == JsonValueKind.Number)
                    outputTokens = outTok.GetInt32();
                if (output.TryGetProperty("model", out var mod) && mod.ValueKind == JsonValueKind.String)
                    modelName = mod.GetString() ?? "unknown";
            }

            // Estimate cost from tokens if available, otherwise use flat rate
            if (inputTokens > 0 || outputTokens > 0)
                estimatedCost = (inputTokens + outputTokens) * 0.000003m; // ~$3/1M tokens avg

            await costTracker.RecordUsageAsync(watchId, blockInstanceId, modelName,
                inputTokens, outputTokens, estimatedCost, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to record LLM cost for block '{BlockId}'", blockInstanceId);
        }
    }

    /// <summary>Mutable cache for the watch's LLM budget within a single pipeline execution.</summary>
    private sealed class BudgetCache
    {
        public bool Loaded;
        public decimal? MonthlyBudget;
    }
}
