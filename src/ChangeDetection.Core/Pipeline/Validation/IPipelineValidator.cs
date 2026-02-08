namespace ChangeDetection.Core.Pipeline.Validation;

/// <summary>
/// Validates a pipeline definition for structural and semantic correctness.
/// </summary>
public interface IPipelineValidator
{
    ValidationResult Validate(PipelineDefinition definition, IBlockRegistry registry);
}
