using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Pipeline;

/// <summary>
/// Validates that LLM-extracted values exist in the original user input.
/// This prevents hallucinated URLs, mangled values, or invented options.
/// Uses only string containment checks - no heuristics or pattern matching.
/// </summary>
public class InputAnchorValidator(ILogger<InputAnchorValidator> logger) : IInputAnchorValidator
{
    /// <inheritdoc />
    public ValidationResult ValidateUrl(string extractedUrl, IEnumerable<string> originalInputs)
    {
        if (string.IsNullOrWhiteSpace(extractedUrl))
        {
            return ValidationResult.Invalid("Extracted URL is empty");
        }

        // Normalize the extracted URL for comparison
        var normalizedExtracted = NormalizeUrl(extractedUrl);
        var inputsList = originalInputs.ToList();

        foreach (var input in inputsList)
        {
            // Check if the URL appears in the original input
            if (input.Contains(extractedUrl, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("URL {Url} found exactly in input", extractedUrl);
                return ValidationResult.Valid(extractedUrl, 1.0f);
            }

            // Check normalized version
            var normalizedInput = input.ToLowerInvariant();
            if (normalizedInput.Contains(normalizedExtracted, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("URL {Url} found (normalized) in input", extractedUrl);
                return ValidationResult.Valid(extractedUrl, 0.95f);
            }

            // Check if URL without protocol exists in input
            var urlWithoutProtocol = RemoveProtocol(extractedUrl);
            if (input.Contains(urlWithoutProtocol, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("URL {Url} found without protocol in input", extractedUrl);
                return ValidationResult.Valid(extractedUrl, 0.9f);
            }
        }

        logger.LogWarning("URL {Url} not found in any original input", extractedUrl);
        return ValidationResult.Invalid($"URL '{extractedUrl}' was not found in any user input. The LLM may have hallucinated or mangled the URL.");
    }

    /// <inheritdoc />
    public ValidationResult ValidateSelection(string userSelection, IEnumerable<PresentedOption> presentedOptions)
    {
        if (string.IsNullOrWhiteSpace(userSelection))
        {
            return ValidationResult.Invalid("User selection is empty");
        }

        var normalizedSelection = userSelection.Trim().ToLowerInvariant();
        var options = presentedOptions.ToList();

        foreach (var option in options)
        {
            // Exact match on option ID
            if (option.OptionId.Equals(userSelection, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("Selection matched option ID {OptionId}", option.OptionId);
                return new ValidationResult
                {
                    IsValid = true,
                    MatchedOption = option,
                    MatchedOriginal = option.DisplayText,
                    MatchConfidence = 1.0f
                };
            }

            // Exact match on display text
            if (option.DisplayText.Equals(userSelection, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("Selection matched option display text {DisplayText}", option.DisplayText);
                return new ValidationResult
                {
                    IsValid = true,
                    MatchedOption = option,
                    MatchedOriginal = option.DisplayText,
                    MatchConfidence = 1.0f
                };
            }

            // Check if selection is contained in or contains the display text
            var normalizedDisplay = option.DisplayText.ToLowerInvariant();
            if (normalizedDisplay.Contains(normalizedSelection) || normalizedSelection.Contains(normalizedDisplay))
            {
                logger.LogDebug("Selection fuzzy matched option {DisplayText}", option.DisplayText);
                return new ValidationResult
                {
                    IsValid = true,
                    MatchedOption = option,
                    MatchedOriginal = option.DisplayText,
                    MatchConfidence = 0.8f
                };
            }
        }

        logger.LogWarning("Selection '{Selection}' did not match any presented option", userSelection);
        return ValidationResult.Invalid($"Selection '{userSelection}' does not match any option that was presented.");
    }

    /// <inheritdoc />
    public ConfigurationValidationResult ValidateConfiguration(
        PartialWatchConfiguration configuration, 
        ConversationSession session)
    {
        var fieldResults = new Dictionary<string, ValidationResult>();
        var invalidFields = new List<string>();

        // Validate URL if present
        if (!string.IsNullOrEmpty(configuration.Url))
        {
            var urlResult = ValidateUrl(configuration.Url, session.OriginalInputs);
            fieldResults["Url"] = urlResult;
            if (!urlResult.IsValid)
            {
                invalidFields.Add("Url");
            }
        }

        // Name and Description are generated by LLM and don't need input anchoring
        // They're allowed to be synthesized from context

        // Check interval - if specified, should be traceable to user input or be a reasonable default
        // We allow LLM to suggest reasonable defaults, so this is not strictly validated

        // Tags - if any are specified, check they appear in user input
        foreach (var tag in configuration.Tags)
        {
            var tagFound = session.OriginalInputs.Any(input => 
                input.Contains(tag, StringComparison.OrdinalIgnoreCase));
            
            if (!tagFound)
            {
                fieldResults[$"Tag:{tag}"] = ValidationResult.Invalid($"Tag '{tag}' not found in user input");
                invalidFields.Add($"Tag:{tag}");
            }
            else
            {
                fieldResults[$"Tag:{tag}"] = ValidationResult.Valid(tag);
            }
        }

        var isValid = invalidFields.Count == 0;
        var message = isValid 
            ? "All configuration values validated against original input"
            : $"The following fields could not be validated: {string.Join(", ", invalidFields)}";

        logger.LogInformation("Configuration validation: {IsValid}, invalid fields: {InvalidFields}", 
            isValid, string.Join(", ", invalidFields));

        return new ConfigurationValidationResult
        {
            IsValid = isValid,
            FieldResults = fieldResults,
            InvalidFields = invalidFields,
            Message = message
        };
    }

    private static string NormalizeUrl(string url)
    {
        return url.Trim().ToLowerInvariant()
            .TrimEnd('/');
    }

    private static string RemoveProtocol(string url)
    {
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url[8..];
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return url[7..];
        return url;
    }
}
