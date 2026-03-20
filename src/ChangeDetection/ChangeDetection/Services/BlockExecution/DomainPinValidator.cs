using System.Net;
using System.Net.Sockets;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.BlockExecution;

/// <summary>
/// Validates URLs against a <see cref="DomainPin"/> to ensure pipelines can only
/// communicate with the user-specified domain. Extends SafeUrlValidator with
/// domain pinning, DNS re-resolution, and redirect validation.
/// </summary>
public class DomainPinValidator(ILogger<DomainPinValidator> logger)
{
    /// <summary>
    /// Validates a URL against the domain pin and SSRF protections.
    /// Returns null if valid, or an error message if blocked.
    /// </summary>
    public string? Validate(string url, DomainPin pin)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "URL must not be empty.";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return $"Malformed URL: '{url}'.";

        // Check scheme
        if (!pin.AllowedSchemes.Contains(uri.Scheme))
        {
            logger.LogWarning("DomainPin blocked scheme {Scheme} for {Url} (allowed: {Allowed})",
                uri.Scheme, url, string.Join(", ", pin.AllowedSchemes));
            return $"Scheme '{uri.Scheme}' is not allowed. Allowed: {string.Join(", ", pin.AllowedSchemes)}.";
        }

        // Check domain against pin patterns
        if (!IsDomainAllowed(uri.Host, pin))
        {
            logger.LogWarning("DomainPin blocked domain {Host} for {Url} (pinned to: {Pin})",
                uri.Host, url, pin.PrimaryDomain);
            return $"Domain '{uri.Host}' is not allowed. Pipeline is pinned to '{pin.PrimaryDomain}'.";
        }

        // Block userinfo in URL (credentials)
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            logger.LogWarning("DomainPin blocked URL with embedded credentials: {Url}", url);
            return "URLs with embedded credentials are not allowed.";
        }

        return null;
    }

    /// <summary>
    /// Validates a URL against the domain pin AND resolves DNS to check for
    /// SSRF via DNS rebinding (domain resolves to private IP).
    /// Use this for runtime validation before making actual requests.
    /// </summary>
    public async Task<string?> ValidateWithDnsResolution(string url, DomainPin pin, CancellationToken ct = default)
    {
        // First do static validation
        var staticError = Validate(url, pin);
        if (staticError is not null)
            return staticError;

        var uri = new Uri(url);

        // Resolve DNS and check resolved IP isn't private/reserved
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);

            foreach (var address in addresses)
            {
                if (IsPrivateOrReserved(address))
                {
                    logger.LogWarning(
                        "DomainPin blocked DNS-resolved private IP {Ip} for host {Host} (URL: {Url})",
                        address, uri.Host, url);
                    return $"Domain '{uri.Host}' resolves to private/reserved IP {address}. Blocked for SSRF protection.";
                }
            }
        }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "DNS resolution failed for {Host}", uri.Host);
            return $"DNS resolution failed for '{uri.Host}': {ex.Message}";
        }

        return null;
    }

    /// <summary>
    /// Validates a redirect target URL against the domain pin.
    /// Redirects to non-pinned domains are blocked to prevent SSRF chains.
    /// </summary>
    public string? ValidateRedirect(string redirectUrl, DomainPin pin, string originalUrl)
    {
        var error = Validate(redirectUrl, pin);
        if (error is not null)
        {
            logger.LogWarning(
                "DomainPin blocked redirect from {Original} to {Redirect}: {Error}",
                originalUrl, redirectUrl, error);
            return $"Redirect blocked: {error} (original URL: {originalUrl})";
        }

        return null;
    }

    /// <summary>
    /// Extracts all URLs from a pipeline definition for static validation.
    /// </summary>
    public IReadOnlyList<(string BlockId, string Url)> ExtractAllUrls(PipelineDefinition pipeline)
    {
        var urls = new List<(string, string)>();

        foreach (var block in pipeline.Blocks)
        {
            if (block.Config is not { ValueKind: System.Text.Json.JsonValueKind.Object } config)
                continue;

            // Check common URL-bearing config fields
            foreach (var fieldName in UrlConfigFields)
            {
                if (config.TryGetProperty(fieldName, out var value) &&
                    value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var urlValue = value.GetString();
                    if (!string.IsNullOrEmpty(urlValue) && Uri.IsWellFormedUriString(urlValue, UriKind.Absolute))
                        urls.Add((block.Id, urlValue));
                }
            }
        }

        return urls;
    }

    /// <summary>
    /// Validates ALL URLs in a pipeline against the domain pin.
    /// Returns list of violations (empty = all valid).
    /// </summary>
    public IReadOnlyList<(string BlockId, string Url, string Error)> ValidatePipeline(
        PipelineDefinition pipeline, DomainPin pin)
    {
        var violations = new List<(string, string, string)>();

        foreach (var (blockId, url) in ExtractAllUrls(pipeline))
        {
            var error = Validate(url, pin);
            if (error is not null)
                violations.Add((blockId, url, error));
        }

        return violations;
    }

    private static bool IsDomainAllowed(string hostname, DomainPin pin)
    {
        var normalized = hostname.ToLowerInvariant();

        foreach (var pattern in pin.AllowedPatterns)
        {
            if (pattern.StartsWith("*."))
            {
                // Wildcard match: *.example.com matches sub.example.com and example.com
                var suffix = pattern[1..]; // ".example.com"
                if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(normalized, pattern[2..], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else
            {
                // Exact match
                if (string.Equals(normalized, pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private static bool IsPrivateOrReserved(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv6LinkLocal) return true;

        if (address.IsIPv4MappedToIPv6)
            return IsPrivateOrReserved(address.MapToIPv4());

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 127.0.0.0/8
            if (bytes[0] == 127) return true;
            // 169.254.0.0/16 (link-local / cloud metadata)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 0.0.0.0/8
            if (bytes[0] == 0) return true;
            // 100.64.0.0/10 (CGNAT)
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
            // 198.18.0.0/15 (benchmarking)
            if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            // fc00::/7 (unique local)
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            // fe80::/10 (link-local)
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true;
        }

        return false;
    }

    private static readonly string[] UrlConfigFields =
        ["url", "urlTemplate", "urlPattern", "baseUrl", "targetUrl", "redirectUrl"];
}
