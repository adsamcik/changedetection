namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Runtime engine that validates and executes a pipeline definition.
/// </summary>
public interface IPipelineExecutor
{
    Task<PipelineExecutionResult> ExecuteAsync(
        PipelineDefinition definition,
        Guid watchId,
        IBlockStateStore stateStore,
        object? page,
        CancellationToken ct = default,
        bool isDryRun = false);
}
