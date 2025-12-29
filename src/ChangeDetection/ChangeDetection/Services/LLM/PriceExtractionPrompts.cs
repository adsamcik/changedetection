namespace ChangeDetection.Services.Llm;

/// <summary>
/// LLM prompts for extracting structured price and stock information.
/// Designed to work with various locales and price formats.
/// </summary>
public static class PriceExtractionPrompts
{
    /// <summary>
    /// System prompt for price extraction that handles various locale formats.
    /// </summary>
    public const string PriceExtractionSystemPrompt = """
        You are a specialized data extraction assistant. Your task is to extract price and stock information from HTML content.

        IMPORTANT RULES:
        1. Extract the numeric price value as a decimal number (e.g., 2499.00 not "2 499 Kč")
        2. Identify the currency from symbols or codes (€, $, £, Kč, CZK, EUR, USD, GBP, etc.)
        3. Classify stock status into exactly one of these categories:
           - InStock: Product is available for purchase
           - OutOfStock: Product is not available
           - LimitedStock: Low quantity warning (e.g., "only 5 left")
           - PreOrder: Not yet released, available for pre-order
           - Discontinued: Product has been discontinued/ended
           - Backorder: Will ship when available
           - Unknown: Cannot determine stock status
        4. Always preserve the original raw text for both price and stock status

        DECIMAL SEPARATOR RULE:
        The LAST separator (comma or period) before the currency symbol is ALWAYS the decimal point.
        Everything before that last separator (commas, periods, spaces, apostrophes) are thousands/grouping separators - ignore them.
        
        Examples:
        - "29,99 €" → 29.99 (comma is last separator = decimal)
        - "1.299,00 €" → 1299.00 (comma is last separator = decimal, period is thousands)
        - "$1,299.00" → 1299.00 (period is last separator = decimal, comma is thousands)
        - "2 499 Kč" → 2499 (space is grouping, no decimal separator)
        - "£1,299.99" → 1299.99 (period is last separator = decimal, comma is thousands)
        - "CHF 1'499.00" → 1499.00 (period is last separator = decimal, apostrophe is thousands)
        - "$50" → 50 (no separator = whole number)
        - "99,00 €" → 99.00 (comma is last separator = decimal)
        - "¥12,800" → 12800 (Yen typically has no decimals, comma is thousands)

        STOCK STATUS EXAMPLES:
        - "Skladem" (Czech) → InStock
        - "Na skladě" (Czech) → InStock
        - "Skladem > 5 ks" → InStock (or LimitedStock if 5 or fewer)
        - "Není skladem" → OutOfStock
        - "UKONČENO" → Discontinued
        - "Vyprodáno" → OutOfStock
        - "In Stock" → InStock
        - "Out of Stock" → OutOfStock
        - "Pre-order" / "Pre-order Now" → PreOrder
        - "Backorder" / "Backordered" → Backorder
        - "Only 3 left" → LimitedStock
        - "Limited availability" → LimitedStock
        - "在庫あり" (Japanese) → InStock
        - "Available" / "Subscribe Now" → InStock
        """;

    /// <summary>
    /// User prompt template for extracting price from a single product page.
    /// </summary>
    public const string SingleProductExtractionPrompt = """
        Extract the price and stock information from this product page HTML.

        HTML Content:
        ```html
        {html}
        ```

        {additionalContext}

        Respond with a JSON object in this exact format:
        ```json
        {
          "price": {
            "value": <decimal number>,
            "currency": "<3-letter currency code>",
            "rawText": "<original price text from page>"
          },
          "stock": {
            "status": "<one of: InStock, OutOfStock, LimitedStock, PreOrder, Discontinued, Backorder, Unknown>",
            "rawText": "<original stock status text from page>",
            "quantity": <number or null if not specified>
          },
          "productName": "<product name if found>",
          "confidence": <0.0 to 1.0>
        }
        ```

        If price or stock cannot be found, set their values to null but still return the structure.
        """;

    /// <summary>
    /// User prompt template for extracting prices from a product listing page.
    /// </summary>
    public const string ProductListingExtractionPrompt = """
        Extract price and stock information for each product in this listing page HTML.

        HTML Content:
        ```html
        {html}
        ```

        {additionalContext}

        Respond with a JSON array where each item follows this format:
        ```json
        [
          {
            "productName": "<product name>",
            "productId": "<SKU, product ID, or unique identifier if available>",
            "price": {
              "value": <decimal number>,
              "currency": "<3-letter currency code>",
              "rawText": "<original price text>"
            },
            "stock": {
              "status": "<one of: InStock, OutOfStock, LimitedStock, PreOrder, Discontinued, Backorder, Unknown>",
              "rawText": "<original stock status text>",
              "quantity": <number or null>
            },
            "url": "<product URL if available>"
          }
        ]
        ```
        """;

    /// <summary>
    /// User prompt for generating CSS/XPath selectors for price elements.
    /// </summary>
    public const string PriceSelectorGenerationPrompt = """
        Analyze this product page HTML and generate CSS selectors for extracting price and stock information.

        HTML Content:
        ```html
        {html}
        ```

        Generate selectors that would reliably extract:
        1. The main product price (not crossed-out/original prices)
        2. The stock/availability status
        3. The product name

        Respond with a JSON object:
        ```json
        {
          "priceSelector": {
            "selector": "<CSS or XPath selector>",
            "type": "css" or "xpath",
            "extractAttribute": null or "<attribute name if value is in an attribute>",
            "confidence": <0.0 to 1.0>
          },
          "stockSelector": {
            "selector": "<CSS or XPath selector>",
            "type": "css" or "xpath",
            "extractAttribute": null or "<attribute name>",
            "confidence": <0.0 to 1.0>
          },
          "nameSelector": {
            "selector": "<CSS or XPath selector>",
            "type": "css" or "xpath",
            "extractAttribute": null or "<attribute name>",
            "confidence": <0.0 to 1.0>
          },
          "notes": "<any important notes about the page structure>"
        }
        ```
        """;

    /// <summary>
    /// Prompt for classifying stock status from raw text.
    /// </summary>
    public const string StockStatusClassificationPrompt = """
        Classify the following stock status text into one of these categories:
        - InStock
        - OutOfStock
        - LimitedStock
        - PreOrder
        - Discontinued
        - Backorder
        - Unknown

        Stock status text: "{stockText}"

        Respond with just the category name, nothing else.
        """;

    /// <summary>
    /// Prompt for parsing price from raw text with locale handling.
    /// </summary>
    public const string PriceParsingPrompt = """
        Parse the following price text and extract the numeric value and currency.

        Price text: "{priceText}"
        Expected locale/region (if known): {locale}

        Respond with a JSON object:
        ```json
        {
          "value": <decimal number>,
          "currency": "<3-letter currency code>",
          "confidence": <0.0 to 1.0>
        }
        ```

        Examples:
        - "2 499 Kč" → {"value": 2499, "currency": "CZK", "confidence": 0.95}
        - "$1,299.00" → {"value": 1299.00, "currency": "USD", "confidence": 0.98}
        - "1.299,00 €" → {"value": 1299.00, "currency": "EUR", "confidence": 0.95}
        - "£999" → {"value": 999, "currency": "GBP", "confidence": 0.95}
        """;

    /// <summary>
    /// Builds the extraction prompt with HTML content.
    /// </summary>
    public static string BuildSingleProductPrompt(string html, string? additionalContext = null)
    {
        return SingleProductExtractionPrompt
            .Replace("{html}", TruncateHtml(html))
            .Replace("{additionalContext}", additionalContext ?? string.Empty);
    }

    /// <summary>
    /// Builds the listing extraction prompt with HTML content.
    /// </summary>
    public static string BuildListingPrompt(string html, string? additionalContext = null)
    {
        return ProductListingExtractionPrompt
            .Replace("{html}", TruncateHtml(html))
            .Replace("{additionalContext}", additionalContext ?? string.Empty);
    }

    /// <summary>
    /// Builds the selector generation prompt with HTML content.
    /// </summary>
    public static string BuildSelectorPrompt(string html)
    {
        return PriceSelectorGenerationPrompt
            .Replace("{html}", TruncateHtml(html));
    }

    /// <summary>
    /// Builds the stock classification prompt.
    /// </summary>
    public static string BuildStockClassificationPrompt(string stockText)
    {
        return StockStatusClassificationPrompt.Replace("{stockText}", stockText);
    }

    /// <summary>
    /// Builds the price parsing prompt.
    /// </summary>
    public static string BuildPriceParsingPrompt(string priceText, string? locale = null)
    {
        return PriceParsingPrompt
            .Replace("{priceText}", priceText)
            .Replace("{locale}", locale ?? "unknown");
    }

    /// <summary>
    /// Truncates HTML to a reasonable size for LLM context.
    /// Removes script, style, and other non-content elements.
    /// </summary>
    private static string TruncateHtml(string html, int maxLength = 50000)
    {
        if (string.IsNullOrEmpty(html))
            return html;

        // Simple truncation - in production, use HtmlAgilityPack to strip non-content
        if (html.Length > maxLength)
        {
            return html[..maxLength] + "\n<!-- HTML truncated -->";
        }

        return html;
    }
}
