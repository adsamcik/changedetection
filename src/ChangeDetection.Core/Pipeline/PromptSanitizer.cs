using System.Security;

namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Sanitizes untrusted content before embedding in LLM prompts.
/// Prevents prompt injection by establishing clear boundaries and truncating oversized input.
/// </summary>
public static class PromptSanitizer
{
    /// <summary>Maximum characters of untrusted content to include in a prompt.</summary>
    public const int MaxContentLength = 50_000;

    /// <summary>
    /// Wraps untrusted content with XML-style boundary markers and truncates if needed.
    /// </summary>
    public static string Sanitize(string untrustedContent, string label = "content")
    {
        // Validate label — must be a simple XML-safe identifier
        var safeLabel = SanitizeLabel(label);

        if (string.IsNullOrEmpty(untrustedContent))
            return $"<{safeLabel}></{safeLabel}>";

        // Strip control characters (except newline, tab, carriage return)
        var cleaned = StripControlCharacters(untrustedContent);

        // Truncate if too long
        if (cleaned.Length > MaxContentLength)
            cleaned = cleaned[..MaxContentLength] + $"\n[TRUNCATED — {untrustedContent.Length - MaxContentLength} characters omitted]";

        var escaped = SecurityElement.Escape(cleaned) ?? string.Empty;
        return $"<{safeLabel}>\n{escaped}\n</{safeLabel}>";
    }

    public static string SanitizeForPrompt(string untrustedContent, string label = "content") =>
        Sanitize(untrustedContent, label);

    /// <summary>
    /// Removes control characters that could confuse LLM parsing.
    /// Preserves newlines (\n), carriage returns (\r), and tabs (\t).
    /// </summary>
    public static string StripControlCharacters(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        return new string(input.Where(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t').ToArray());
    }

    /// <summary>
    /// Ensures label is a safe XML tag name — alphanumeric, underscores, hyphens only.
    /// </summary>
    private static string SanitizeLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return "content";

        var safe = new string(label.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
        return string.IsNullOrEmpty(safe) ? "content" : safe;
    }
}
