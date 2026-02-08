namespace ChangeDetection.Core.Pipeline.Validation;

/// <summary>
/// Result of pipeline validation containing errors (hard fail) and warnings (soft).
/// </summary>
public record ValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public IReadOnlyList<ValidationError> Errors { get; init; } = [];
    public IReadOnlyList<ValidationWarning> Warnings { get; init; } = [];

    public static ValidationResult Valid() => new();

    public static ValidationResult WithErrors(params ValidationError[] errors) =>
        new() { Errors = errors };
}

public record ValidationError(string Code, string Message, string? BlockId = null);

public record ValidationWarning(string Code, string Message, string? BlockId = null);
