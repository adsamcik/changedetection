using System.Text.Json;
using System.Text.RegularExpressions;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Pipeline;

namespace ChangeDetection.Services.GroupWatch;

public interface IPortalDiscoveryAnalyzer
{
    Task<List<PortalSuggestion>> AnalyzeForNewPortalsAsync(
        Guid sourceWatchId,
        JsonElement pipelineOutput,
        CancellationToken ct = default);
}

public record PortalSuggestion(
    string Url,
    string Domain,
    string? DetectedPlatform,
    string Reason,
    Guid SourceWatchId);

public partial class PortalDiscoveryAnalyzer(
    IRepository<WatchedSite> watchRepo,
    ILogger<PortalDiscoveryAnalyzer> logger) : IPortalDiscoveryAnalyzer
{
    private static readonly string[] KnownAtsDomains =
    [
        "myworkdayjobs.com",
        "teamtailor.com",
        "workable.com",
        "greenhouse.io",
        "lever.co"
    ];

    private static readonly string[] CareerPathKeywords =
    [
        "careers",
        "jobs",
        "vacancies",
        "positions",
        "openings",
        "join-us"
    ];

    private static readonly string[] DocumentExtensions =
    [
        ".pdf",
        ".doc",
        ".docx",
        ".xls",
        ".xlsx",
        ".ppt",
        ".pptx",
        ".zip"
    ];

    private static readonly HashSet<string> SocialDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "linkedin.com",
        "facebook.com",
        "instagram.com",
        "x.com",
        "twitter.com",
        "youtube.com",
        "tiktok.com"
    };

    public async Task<List<PortalSuggestion>> AnalyzeForNewPortalsAsync(
        Guid sourceWatchId,
        JsonElement pipelineOutput,
        CancellationToken ct = default)
    {
        var sourceWatch = await watchRepo.GetByIdAsync(sourceWatchId, ct);
        if (sourceWatch is null || !Uri.TryCreate(sourceWatch.Url, UriKind.Absolute, out var sourceUri))
        {
            logger.LogDebug("Skipping portal discovery for watch {WatchId}: source watch or URL is invalid", sourceWatchId);
            return [];
        }

        var sourceDomain = NormalizeDomain(sourceUri.Host);
        var watchedSites = (await watchRepo.GetAllAsync(ct)).ToList();
        var watchedPortalKeys = watchedSites
            .Select(site => CreatePortalKey(site.Url))
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var suggestions = new List<PortalSuggestion>();
        var seenPortalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawCandidate in EnumerateCandidateUrls(pipelineOutput))
        {
            if (!TryResolveCandidateUrl(rawCandidate, sourceUri, out var resolvedUri))
                continue;

            var domain = NormalizeDomain(resolvedUri.Host);
            if (string.IsNullOrWhiteSpace(domain) || string.Equals(domain, sourceDomain, StringComparison.OrdinalIgnoreCase))
                continue;

            if (IsDocumentLink(resolvedUri) || IsSocialLink(domain))
                continue;

            var detectedPlatform = SetupFlowEnhancements.DetectPlatformFromUrl(resolvedUri.ToString());
            var canonicalUri = CanonicalizePortalUri(resolvedUri, detectedPlatform);
            if (!LooksLikeCareerPortal(canonicalUri, detectedPlatform, out var reason))
                continue;

            var portalKey = CreatePortalKey(canonicalUri.ToString());
            if (string.IsNullOrWhiteSpace(portalKey) ||
                watchedPortalKeys.Contains(portalKey) ||
                seenPortalKeys.Contains(portalKey))
            {
                continue;
            }

            seenPortalKeys.Add(portalKey);
            suggestions.Add(new PortalSuggestion(
                canonicalUri.ToString(),
                NormalizeDomain(canonicalUri.Host),
                detectedPlatform,
                reason,
                sourceWatchId));
        }

        return suggestions;
    }

    private static IEnumerable<string> EnumerateCandidateUrls(JsonElement element, string? propertyName = null)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var candidate in EnumerateCandidateUrls(property.Value, property.Name))
                        yield return candidate;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var candidate in EnumerateCandidateUrls(item, propertyName))
                        yield return candidate;
                }
                break;

            case JsonValueKind.String:
            {
                var value = element.GetString();
                if (string.IsNullOrWhiteSpace(value))
                    yield break;

                if (LooksLikeUrlProperty(propertyName))
                    yield return value;

                foreach (Match match in HrefRegex().Matches(value))
                {
                    if (match.Groups["url"].Success)
                        yield return match.Groups["url"].Value;
                }

                foreach (Match match in UrlRegex().Matches(value))
                {
                    if (match.Groups["url"].Success)
                        yield return match.Groups["url"].Value;
                }

                break;
            }
        }
    }

    private static bool TryResolveCandidateUrl(string rawCandidate, Uri sourceUri, out Uri resolvedUri)
    {
        resolvedUri = default!;

        if (string.IsNullOrWhiteSpace(rawCandidate))
            return false;

        var candidate = rawCandidate.Trim().Trim('"', '\'');
        if (candidate.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith('#'))
        {
            return false;
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out resolvedUri))
            return string.Equals(resolvedUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(resolvedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

        if (Uri.TryCreate(sourceUri, candidate, out resolvedUri))
            return string.Equals(resolvedUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(resolvedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private static bool LooksLikeCareerPortal(Uri uri, string? detectedPlatform, out string reason)
    {
        if (!string.IsNullOrWhiteSpace(detectedPlatform))
        {
            reason = $"Known ATS platform detected: {detectedPlatform}.";
            return true;
        }

        if (KnownAtsDomains.Any(domain => uri.Host.Contains(domain, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "Known applicant tracking system domain detected.";
            return true;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var matchedKeyword = segments.FirstOrDefault(segment =>
            CareerPathKeywords.Any(keyword => string.Equals(segment, keyword, StringComparison.OrdinalIgnoreCase)));

        if (matchedKeyword is not null)
        {
            reason = $"Career page path matched '/{matchedKeyword}'.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static Uri CanonicalizePortalUri(Uri uri, string? detectedPlatform)
    {
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (segments.Count == 0)
            return StripQueryAndFragment(uri, "/");

        if (string.Equals(detectedPlatform, "workday", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Count >= 2 && !segments[0].Equals("wday", StringComparison.OrdinalIgnoreCase))
                return StripQueryAndFragment(uri, "/" + string.Join('/', segments.Take(2)));

            if (segments.Count >= 5 && segments[0].Equals("wday", StringComparison.OrdinalIgnoreCase))
                return StripQueryAndFragment(uri, "/" + string.Join('/', segments.Take(5)));
        }

        if (string.Equals(detectedPlatform, "teamtailor", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Count >= 2 && segments[0].Equals("careers", StringComparison.OrdinalIgnoreCase) && segments[1].Equals("jobs", StringComparison.OrdinalIgnoreCase))
                return StripQueryAndFragment(uri, "/careers/jobs");

            if (segments[0].Equals("jobs", StringComparison.OrdinalIgnoreCase))
                return StripQueryAndFragment(uri, "/jobs");
        }

        if (string.Equals(detectedPlatform, "workable", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(detectedPlatform, "greenhouse", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(detectedPlatform, "lever", StringComparison.OrdinalIgnoreCase))
        {
            return StripQueryAndFragment(uri, "/" + segments[0]);
        }

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (!CareerPathKeywords.Any(keyword => string.Equals(keyword, segment, StringComparison.OrdinalIgnoreCase)))
                continue;

            var keepCount = i + 1;
            if (i + 1 < segments.Count &&
                CareerPathKeywords.Any(keyword => string.Equals(keyword, segments[i + 1], StringComparison.OrdinalIgnoreCase)))
            {
                keepCount++;
            }

            return StripQueryAndFragment(uri, "/" + string.Join('/', segments.Take(keepCount)));
        }

        return StripQueryAndFragment(uri, uri.AbsolutePath);
    }

    private static Uri StripQueryAndFragment(Uri uri, string path)
    {
        var builder = new UriBuilder(uri)
        {
            Path = string.IsNullOrWhiteSpace(path) ? "/" : path,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri;
    }

    private static bool LooksLikeUrlProperty(string? propertyName) =>
        !string.IsNullOrWhiteSpace(propertyName) &&
        (propertyName.Contains("url", StringComparison.OrdinalIgnoreCase) ||
         propertyName.Contains("href", StringComparison.OrdinalIgnoreCase) ||
         propertyName.Contains("link", StringComparison.OrdinalIgnoreCase));

    private static bool IsDocumentLink(Uri uri) =>
        DocumentExtensions.Any(extension =>
            uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static bool IsSocialLink(string domain) =>
        SocialDomains.Any(socialDomain =>
            domain.Equals(socialDomain, StringComparison.OrdinalIgnoreCase) ||
            domain.EndsWith("." + socialDomain, StringComparison.OrdinalIgnoreCase));

    private static string CreatePortalKey(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return string.Empty;

        var host = NormalizeDomain(uri.Host);
        if (string.IsNullOrWhiteSpace(host))
            return string.Empty;

        var platform = SetupFlowEnhancements.DetectPlatformFromUrl(url);
        var canonical = CanonicalizePortalUri(uri, platform);
        var path = canonical.AbsolutePath.TrimEnd('/');

        return string.IsNullOrWhiteSpace(path) || path == "/"
            ? host
            : $"{host}{path}";
    }

    private static string NormalizeDomain(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return string.Empty;

        var normalized = host.Trim().ToLowerInvariant();
        return normalized.StartsWith("www.", StringComparison.Ordinal)
            ? normalized[4..]
            : normalized;
    }

    [GeneratedRegex("""href\s*=\s*["'](?<url>[^"'#>]+)["']""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HrefRegex();

    [GeneratedRegex("""(?<url>https?://[^\s"'<>]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();
}
