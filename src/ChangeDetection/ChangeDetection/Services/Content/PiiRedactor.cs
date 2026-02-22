using System.Text.RegularExpressions;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Content;

/// <summary>
/// Regex-based PII redactor that detects and replaces common PII patterns.
/// Patterns: email addresses, phone numbers, SSNs, credit card numbers, IP addresses.
/// </summary>
public partial class PiiRedactor : IPiiRedactor
{
    private static readonly (string Name, Regex Pattern, string Replacement)[] Patterns =
    [
        ("email", EmailRegex(), "[REDACTED-EMAIL]"),
        ("ssn", SsnRegex(), "[REDACTED-SSN]"),
        ("credit-card", CreditCardRegex(), "[REDACTED-CC]"),
        ("phone", PhoneRegex(), "[REDACTED-PHONE]"),
        ("ipv4", Ipv4Regex(), "[REDACTED-IP]")
    ];

    public PiiRedactionResult Redact(string content)
    {
        var result = content;
        var total = 0;
        var types = new List<string>();

        foreach (var (name, pattern, replacement) in Patterns)
        {
            var count = pattern.Matches(result).Count;
            if (count > 0)
            {
                result = pattern.Replace(result, replacement);
                total += count;
                types.Add(name);
            }
        }

        return new PiiRedactionResult(result, total, types);
    }

    [GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b\d{3}[-.\s]?\d{2}[-.\s]?\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex SsnRegex();

    [GeneratedRegex(@"\b(?:\d{4}[-.\s]?){3}\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex CreditCardRegex();

    [GeneratedRegex(@"(?<!\d)(?:\+?1[-.\s]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}(?!\d)", RegexOptions.Compiled)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\b", RegexOptions.Compiled)]
    private static partial Regex Ipv4Regex();
}
