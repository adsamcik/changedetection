namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Redacts personally identifiable information (PII) from text content.
/// </summary>
public interface IPiiRedactor
{
    /// <summary>
    /// Redacts detected PII patterns from the input text.
    /// </summary>
    PiiRedactionResult Redact(string content);
}

/// <summary>
/// Result of PII redaction including the cleaned text and detection counts.
/// </summary>
public record PiiRedactionResult(string RedactedContent, int RedactionsApplied, List<string> RedactedTypes);
