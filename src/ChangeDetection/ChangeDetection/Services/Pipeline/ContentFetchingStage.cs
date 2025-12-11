using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Pipeline;

/// <summary>
/// Stage 2: Fetches content from the URL and prepares it for analysis.
/// </summary>
public class ContentFetchingStage(
    IContentFetcher contentFetcher,
    IContentExtractor contentExtractor,
    ILogger<ContentFetchingStage> logger)
{
    /// <summary>
    /// Fetches content from the URL.
    /// </summary>
    public async Task<FetchedContent> FetchAsync(
        string url, 
        bool useJavaScript = false,
        int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        logger.LogInformation("Fetching content from {Url}, JS={UseJs}", url, useJavaScript);

        try
        {
            var options = new FetchOptions
            {
                UseJavaScript = useJavaScript,
                TimeoutSeconds = timeoutSeconds,
                CaptureScreenshot = false,
                WaitAfterLoadMs = useJavaScript ? 2000 : 0
            };

            var result = await contentFetcher.FetchAsync(url, options, ct);

            if (!result.IsSuccess || string.IsNullOrEmpty(result.Html))
            {
                return new FetchedContent
                {
                    Url = url,
                    IsSuccess = false,
                    ErrorMessage = result.ErrorMessage ?? $"Failed to fetch content (HTTP {result.HttpStatusCode})",
                    UsedJavaScript = useJavaScript
                };
            }

            // Process the HTML
            var cleanedHtml = contentExtractor.CleanHtml(result.Html);
            var textContent = contentExtractor.ExtractText(result.Html);
            var title = contentExtractor.ExtractTitle(result.Html);

            // Truncate for LLM processing (keep it manageable)
            var truncatedHtml = TruncateForLlm(cleanedHtml, maxChars: 50000);
            var truncatedText = TruncateForLlm(textContent, maxChars: 20000);

            return new FetchedContent
            {
                Url = url,
                Html = result.Html,
                CleanedHtml = truncatedHtml,
                TextContent = truncatedText,
                Title = title,
                IsSuccess = true,
                FetchDurationMs = result.DurationMs,
                UsedJavaScript = useJavaScript
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching {Url}", url);
            return new FetchedContent
            {
                Url = url,
                IsSuccess = false,
                ErrorMessage = $"Fetch error: {ex.Message}",
                UsedJavaScript = useJavaScript
            };
        }
    }

    /// <summary>
    /// Re-fetches with JavaScript if initial fetch seems incomplete.
    /// </summary>
    public async Task<FetchedContent> RetryWithJavaScriptAsync(
        FetchedContent previousResult,
        int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        if (previousResult.UsedJavaScript)
        {
            // Already tried with JS
            return previousResult;
        }

        logger.LogInformation("Retrying {Url} with JavaScript rendering", previousResult.Url);
        return await FetchAsync(previousResult.Url, useJavaScript: true, timeoutSeconds, ct);
    }

    /// <summary>
    /// Determines if content seems to need JavaScript rendering.
    /// </summary>
    public bool ShouldUseJavaScript(FetchedContent content)
    {
        if (content.UsedJavaScript || !content.IsSuccess)
            return false;

        var text = content.TextContent ?? "";
        var html = content.Html ?? "";

        // Very little text content
        if (text.Length < 100)
            return true;

        // Contains common SPA framework indicators
        var spaIndicators = new[]
        {
            "ng-app", "ng-controller", // Angular
            "data-reactroot", "__NEXT_DATA__", // React/Next.js
            "data-vue-app", "v-app", // Vue
            "data-svelte", // Svelte
            "window.__NUXT__", // Nuxt
            "<noscript>", // Often indicates JS-dependent content
        };

        return spaIndicators.Any(indicator => 
            html.Contains(indicator, StringComparison.OrdinalIgnoreCase));
    }

    private static string TruncateForLlm(string content, int maxChars)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxChars)
            return content;

        // Try to truncate at a paragraph/section boundary
        var truncated = content[..maxChars];
        
        // Find last complete paragraph or significant break
        var lastBreak = truncated.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (lastBreak > maxChars / 2)
        {
            truncated = truncated[..lastBreak];
        }
        else
        {
            // Find last sentence
            var lastSentence = truncated.LastIndexOf(". ", StringComparison.Ordinal);
            if (lastSentence > maxChars / 2)
            {
                truncated = truncated[..(lastSentence + 1)];
            }
        }

        return truncated + "\n\n[Content truncated...]";
    }
}
