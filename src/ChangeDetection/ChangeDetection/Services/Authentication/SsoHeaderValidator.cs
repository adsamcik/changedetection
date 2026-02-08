namespace ChangeDetection.Services.Authentication;

/// <summary>
/// Validates and sanitizes SSO proxy headers to prevent header injection attacks.
/// </summary>
public static class SsoHeaderValidator
{
    private const int DefaultMaxLength = 256;

    /// <summary>
    /// Sanitizes an SSO header value by removing control characters and limiting length.
    /// Returns null if the value is empty or contains only invalid characters.
    /// </summary>
    public static string? Sanitize(string? value, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        // Remove control characters (potential injection vectors: null bytes, newlines, etc.)
        var sanitized = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();

        if (sanitized.Length > maxLength)
            sanitized = sanitized[..maxLength];

        return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
    }

    /// <summary>
    /// Checks whether a raw header value contains control characters (null bytes, newlines, etc.)
    /// that indicate a potential injection attempt.
    /// </summary>
    public static bool ContainsControlCharacters(string? value)
    {
        return value is not null && value.Any(char.IsControl);
    }

    /// <summary>
    /// Validates an email address format using <see cref="System.Net.Mail.MailAddress"/>.
    /// </summary>
    public static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        try
        {
            var addr = new System.Net.Mail.MailAddress(email);
            return addr.Address == email.Trim();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validates a username format. Allows alphanumeric characters, dots, hyphens, underscores, and @.
    /// </summary>
    public static bool IsValidUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return false;

        return username.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '@');
    }
}
