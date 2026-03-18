namespace ChangeDetection.Services.Scraping;

public static class BrowserHeaders
{
    private static readonly Dictionary<string, string> _chrome = new()
    {
        ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36",
        ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
        ["Accept-Language"] = "en-US,en;q=0.9",
        ["Accept-Encoding"] = "gzip, deflate, br",
        ["Connection"] = "keep-alive",
        ["Upgrade-Insecure-Requests"] = "1",
        ["Sec-Fetch-Dest"] = "document",
        ["Sec-Fetch-Mode"] = "navigate",
        ["Sec-Fetch-Site"] = "none",
        ["Sec-Fetch-User"] = "?1",
    };

    public static IReadOnlyDictionary<string, string> Chrome => _chrome;
}

public static class LightweightFetchHeuristics
{
    public static bool NeedsJavaScript(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return false;

        if (html.Contains("__NEXT_DATA__", StringComparison.OrdinalIgnoreCase) ||
            html.Contains("window.__NUXT__", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (html.Length >= 500)
            return false;

        return html.Contains("<div id=\"app\">", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("<div id='app'>", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("<div id=\"root\">", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("<div id='root'>", StringComparison.OrdinalIgnoreCase);
    }
}
