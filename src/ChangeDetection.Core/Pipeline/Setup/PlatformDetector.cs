namespace ChangeDetection.Core.Pipeline.Setup;

public record DetectedPlatform(string PlatformId, string PlatformName, float Confidence);

public interface IPlatformDetector
{
    /// <summary>Detect the platform from URL pattern alone (fast, no HTTP needed).</summary>
    DetectedPlatform? DetectFromUrl(string url);

    /// <summary>Detect from URL + HTML content (more accurate, requires fetch).</summary>
    DetectedPlatform? DetectFromContent(string url, string html);
}

public class PlatformDetector : IPlatformDetector
{
    public DetectedPlatform? DetectFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host.ToLowerInvariant();
        var fullUrl = uri.AbsoluteUri.ToLowerInvariant();

        if (host.Contains("myworkdayjobs.com", StringComparison.Ordinal))
            return Detect("workday", "Workday", 0.99f);

        if (host.Equals("jobs.lever.co", StringComparison.Ordinal) ||
            host.EndsWith(".lever.co", StringComparison.Ordinal))
            return Detect("lever", "Lever", 0.98f);

        if (host.Equals("boards.greenhouse.io", StringComparison.Ordinal) ||
            host.EndsWith(".greenhouse.io", StringComparison.Ordinal))
            return Detect("greenhouse", "Greenhouse", 0.98f);

        if (host.EndsWith(".teamtailor.com", StringComparison.Ordinal))
            return Detect("teamtailor", "Teamtailor", 0.98f);

        if (host.Equals("apply.workable.com", StringComparison.Ordinal) ||
            host.EndsWith(".workable.com", StringComparison.Ordinal))
            return Detect("workable", "Workable", 0.97f);

        if (host.EndsWith(".myshopify.com", StringComparison.Ordinal))
            return Detect("shopify", "Shopify", 0.98f);

        if (fullUrl.Contains("/wp-content/", StringComparison.Ordinal) ||
            fullUrl.Contains("/wp-json/", StringComparison.Ordinal))
            return Detect("wordpress", "WordPress", 0.96f);

        if (host.EndsWith(".bamboohr.com", StringComparison.Ordinal))
            return Detect("bamboohr", "BambooHR", 0.97f);

        if (host.EndsWith(".easycruit.com", StringComparison.Ordinal))
            return Detect("easycruit", "EasyCruit", 0.97f);

        return null;
    }

    public DetectedPlatform? DetectFromContent(string url, string html)
    {
        var urlDetection = DetectFromUrl(url);
        if (string.IsNullOrWhiteSpace(html))
            return urlDetection;

        var htmlLower = html.ToLowerInvariant();
        var contentDetection = DetectFromHtml(htmlLower);

        if (contentDetection is null)
            return urlDetection;

        if (urlDetection is null || contentDetection.Confidence > urlDetection.Confidence)
            return contentDetection;

        return urlDetection;
    }

    private static DetectedPlatform? DetectFromHtml(string htmlLower)
    {
        if (htmlLower.Contains("shopify.theme", StringComparison.Ordinal))
            return Detect("shopify", "Shopify", 0.95f);

        if (htmlLower.Contains("wp-content", StringComparison.Ordinal))
            return Detect("wordpress", "WordPress", 0.93f);

        if (ContainsLinkHint(htmlLower, "greenhouse"))
            return Detect("greenhouse", "Greenhouse", 0.84f);

        if (ContainsLinkHint(htmlLower, "lever"))
            return Detect("lever", "Lever", 0.84f);

        return null;
    }

    private static bool ContainsLinkHint(string htmlLower, string hint) =>
        htmlLower.Contains($"href=\"https://{hint}", StringComparison.Ordinal) ||
        htmlLower.Contains($".{hint}.io", StringComparison.Ordinal);

    private static DetectedPlatform Detect(string platformId, string platformName, float confidence) =>
        new(platformId, platformName, confidence);
}
