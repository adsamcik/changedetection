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
        if (string.IsNullOrEmpty(untrustedContent))
            return $"<{label}></{label}>";

        // Strip control characters (except newline, tab, carriage return)
        var cleaned = StripControlCharacters(untrustedContent);

        // Truncate if too long
        if (cleaned.Length > MaxContentLength)
            cleaned = cleaned[..MaxContentLength] + $"\n[TRUNCATED — {untrustedContent.Length - MaxContentLength} characters omitted]";

        return $"<{label}>\n{cleaned}\n</{label}>";
    }

    /// <summary>
    /// Removes control characters that could confuse LLM parsing.
    /// Preserves newlines (\n), carriage returns (\r), and tabs (\t).
    /// </summary>
    public static string StripControlCharacters(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        return new string(input.Where(c => !char.IsControl(c) || c == '\n' || c == '\r' || c == '\t').ToArray());
    }
}
