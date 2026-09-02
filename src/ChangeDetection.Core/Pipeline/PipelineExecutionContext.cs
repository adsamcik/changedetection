namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Flows pipeline run context through async execution using AsyncLocal.
/// Can be set by an orchestrator at run start and is read by LLM logging for correlation.
/// </summary>
public static class PipelineExecutionContext
{
    private static readonly AsyncLocal<Guid?> _currentRunId = new();

    /// <summary>
    /// The pipeline run ID for the current async execution context.
    /// Null when not executing within a pipeline run.
    /// </summary>
    public static Guid? CurrentPipelineRunId
    {
        get => _currentRunId.Value;
        set => _currentRunId.Value = value;
    }
}
