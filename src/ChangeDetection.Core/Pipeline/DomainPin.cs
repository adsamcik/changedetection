namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Immutable domain constraint set from user input (never from page content or LLM output).
/// Every URL in every pipeline block is validated against this pin.
/// This is the primary security kill switch — even if the LLM is fully compromised by
/// prompt injection, pipelines can only communicate with the user-specified domain.
/// </summary>
public sealed record DomainPin
{
    /// <summary>The primary domain extracted from the user's input URL.</summary>
    public required string PrimaryDomain { get; init; }

    /// <summary>Allowed domain patterns (primary + wildcard subdomains).</summary>
    public required IReadOnlyList<string> AllowedPatterns { get; init; }

    /// <summary>Allowed schemes (default: https only, http allowed explicitly).</summary>
    public required IReadOnlySet<string> AllowedSchemes { get; init; }

    /// <summary>
    /// Provenance marker — always "user_input". Documents that this pin
    /// was derived from the user's original URL, not from page content.
    /// </summary>
    public string Source => "user_input";

    /// <summary>
    /// Creates a DomainPin from a user-provided URL.
    /// Extracts the hostname and allows the domain + all subdomains.
    /// </summary>
    public static DomainPin FromUserUrl(string userUrl)
    {
        if (string.IsNullOrWhiteSpace(userUrl))
            throw new ArgumentException("URL must not be empty.", nameof(userUrl));

        if (!Uri.TryCreate(userUrl, UriKind.Absolute, out var uri))
            throw new ArgumentException($"Invalid URL: '{userUrl}'", nameof(userUrl));

        var domain = uri.Host.ToLowerInvariant();
        var scheme = uri.Scheme.ToLowerInvariant();

        // Allow both http and https if the user's URL uses either
        var schemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "https" };
        if (string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase))
            schemes.Add("http");

        return new DomainPin
        {
            PrimaryDomain = domain,
            AllowedPatterns = [domain, $"*.{domain}"],
            AllowedSchemes = schemes,
        };
    }

    /// <summary>
    /// Creates a DomainPin that allows multiple related domains.
    /// Useful for ATS platforms where the API is on a different subdomain
    /// (e.g., careers.company.com + company.wd3.myworkdayjobs.com).
    /// </summary>
    public static DomainPin FromUserUrlWithAdditionalDomains(string userUrl, IEnumerable<string> additionalDomains)
    {
        var basePin = FromUserUrl(userUrl);
        var patterns = new List<string>(basePin.AllowedPatterns);

        foreach (var domain in additionalDomains)
        {
            var normalized = domain.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(normalized))
            {
                patterns.Add(normalized);
                patterns.Add($"*.{normalized}");
            }
        }

        return basePin with { AllowedPatterns = patterns.AsReadOnly() };
    }
}
