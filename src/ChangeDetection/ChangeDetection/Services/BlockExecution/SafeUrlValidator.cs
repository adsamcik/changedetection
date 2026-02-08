using System.Net;
using System.Net.Sockets;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.BlockExecution;

/// <summary>
/// Validates URLs to prevent SSRF attacks by blocking private/internal addresses,
/// non-HTTP schemes, and dangerous hostnames.
/// </summary>
public class SafeUrlValidator(ILogger<SafeUrlValidator> logger) : IUrlValidator
{
    private static readonly (byte[] Network, int PrefixLength)[] BlockedCidrRanges =
    [
        (new byte[] { 10, 0, 0, 0 }, 8),          // 10.0.0.0/8
        (new byte[] { 172, 16, 0, 0 }, 12),        // 172.16.0.0/12
        (new byte[] { 192, 168, 0, 0 }, 16),       // 192.168.0.0/16
        (new byte[] { 127, 0, 0, 0 }, 8),          // 127.0.0.0/8
        (new byte[] { 169, 254, 0, 0 }, 16),       // 169.254.0.0/16
        (new byte[] { 0, 0, 0, 0 }, 8),            // 0.0.0.0/8
    ];

    private static readonly string[] BlockedHostSuffixes = [".local", ".internal"];

    public string? Validate(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            logger.LogWarning("URL validation failed: empty or null URL");
            return "URL must not be empty.";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            logger.LogWarning("URL validation failed: malformed URL {Url}", url);
            return $"Malformed URL: '{url}'.";
        }

        // Only allow http and https schemes
        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("URL validation blocked scheme {Scheme} for {Url}", uri.Scheme, url);
            return $"Scheme '{uri.Scheme}' is not allowed. Only http and https are permitted.";
        }

        var host = uri.Host;

        // Block localhost
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("URL validation blocked localhost: {Url}", url);
            return "Requests to localhost are not allowed.";
        }

        // Block dangerous TLDs
        foreach (var suffix in BlockedHostSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("URL validation blocked host with suffix {Suffix}: {Url}", suffix, url);
                return $"Requests to '{suffix}' domains are not allowed.";
            }
        }

        // Check if host is an IP address (IPv4 or IPv6)
        if (IPAddress.TryParse(host, out var ipAddress))
        {
            var blockReason = CheckIpAddress(ipAddress);
            if (blockReason is not null)
            {
                logger.LogWarning("URL validation blocked IP {Ip}: {Reason} for {Url}", ipAddress, blockReason, url);
                return blockReason;
            }
        }

        return null;
    }

    private static string? CheckIpAddress(IPAddress address)
    {
        // Block IPv6 loopback (::1)
        if (IPAddress.IsLoopback(address))
            return "Requests to loopback addresses are not allowed.";

        // Block IPv6 link-local (fe80::/10)
        if (address.IsIPv6LinkLocal)
            return "Requests to link-local IPv6 addresses are not allowed.";

        // For IPv4 addresses, check against blocked CIDR ranges
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var addressBytes = address.GetAddressBytes();

            foreach (var (network, prefixLength) in BlockedCidrRanges)
            {
                if (IsInCidrRange(addressBytes, network, prefixLength))
                    return $"Requests to private/reserved IP range ({FormatCidr(network, prefixLength)}) are not allowed.";
            }
        }

        // Block IPv4-mapped IPv6 addresses (e.g., ::ffff:127.0.0.1)
        if (address.IsIPv4MappedToIPv6)
        {
            var mapped = address.MapToIPv4();
            return CheckIpAddress(mapped);
        }

        return null;
    }

    private static bool IsInCidrRange(byte[] address, byte[] network, int prefixLength)
    {
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (address[i] != network[i])
                return false;
        }

        if (remainingBits > 0)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            if ((address[fullBytes] & mask) != (network[fullBytes] & mask))
                return false;
        }

        return true;
    }

    private static string FormatCidr(byte[] network, int prefixLength) =>
        $"{string.Join('.', network)}/{prefixLength}";
}
