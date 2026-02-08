namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Validates URLs to prevent SSRF (Server-Side Request Forgery) attacks.
/// </summary>
public interface IUrlValidator
{
    /// <summary>
    /// Returns null if the URL is safe to fetch, or an error message describing why it is blocked.
    /// </summary>
    string? Validate(string url);
}
