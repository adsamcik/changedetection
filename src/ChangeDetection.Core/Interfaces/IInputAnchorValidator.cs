namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Validates that extracted values exist in the original user input.
/// This is the only validation allowed - verifying the LLM didn't hallucinate or mangle data.
/// No heuristics or pattern matching allowed.
/// </summary>
public interface IInputAnchorValidator
{
    /// <summary>
    /// Validates that a URL extracted by the LLM exists in the original inputs.
    /// </summary>
    /// <param name="extractedUrl">URL extracted by the LLM.</param>
    /// <param name="originalInputs">All original user inputs from the session.</param>
    /// <returns>Validation result with details.</returns>
    ValidationResult ValidateUrl(string extractedUrl, IEnumerable<string> originalInputs);

    /// <summary>
    /// Validates that a user selection matches a presented option.
    /// </summary>
    /// <param name="userSelection">What the user selected/typed.</param>
    /// <param name="presentedOptions">Options that were shown to the user.</param>
    /// <returns>Validation result with matched option if found.</returns>
    ValidationResult ValidateSelection(string userSelection, IEnumerable<PresentedOption> presentedOptions);

    /// <summary>
    /// Validates that extracted configuration values can be traced back to user input.
    /// </summary>
    /// <param name="configuration">Partial configuration to validate.</param>
    /// <param name="session">Session containing all original inputs and presented options.</param>
    /// <returns>Validation result for the entire configuration.</returns>
    ConfigurationValidationResult ValidateConfiguration(PartialWatchConfiguration configuration, ConversationSession session);
}

/// <summary>
/// Result of a single validation check.
/// </summary>
public class ValidationResult
{
    /// <summary>
    /// Whether the validation passed.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// The original value that was found (may differ slightly in formatting).
    /// </summary>
    public string? MatchedOriginal { get; init; }

    /// <summary>
    /// Explanation of the validation result.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// If validating a selection, the matched presented option.
    /// </summary>
    public PresentedOption? MatchedOption { get; init; }

    /// <summary>
    /// Confidence in the match (1.0 = exact, lower = fuzzy).
    /// </summary>
    public float MatchConfidence { get; init; }

    public static ValidationResult Valid(string? matchedOriginal = null, float confidence = 1.0f) => new()
    {
        IsValid = true,
        MatchedOriginal = matchedOriginal,
        MatchConfidence = confidence
    };

    public static ValidationResult Invalid(string message) => new()
    {
        IsValid = false,
        Message = message,
        MatchConfidence = 0
    };
}

/// <summary>
/// Result of validating an entire configuration.
/// </summary>
public class ConfigurationValidationResult
{
    /// <summary>
    /// Whether all required fields validated successfully.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Individual field validation results.
    /// </summary>
    public Dictionary<string, ValidationResult> FieldResults { get; init; } = [];

    /// <summary>
    /// Fields that failed validation.
    /// </summary>
    public List<string> InvalidFields { get; init; } = [];

    /// <summary>
    /// Overall message.
    /// </summary>
    public string? Message { get; init; }
}
