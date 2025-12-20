using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Llm;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services;

/// <summary>
/// Service for extracting, storing, and evaluating price/stock data.
/// Orchestrates LLM extraction → storage → threshold evaluation → notifications.
/// </summary>
public class PriceTrackingService(
    ILlmProviderChain llmProvider,
    IPriceHistoryRepository priceHistoryRepository,
    IAlertThresholdEvaluator alertEvaluator,
    INotificationService notificationService,
    ILogger<PriceTrackingService> logger) : IPriceTrackingService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Extracts price and stock information from HTML content using LLM.
    /// </summary>
    public async Task<PriceExtractionResult?> ExtractPriceAsync(
        string html,
        string? additionalContext = null,
        CancellationToken ct = default)
    {
        try
        {
            var prompt = $"{PriceExtractionPrompts.PriceExtractionSystemPrompt}\n\n{PriceExtractionPrompts.BuildSingleProductPrompt(html, additionalContext)}";
            var response = await llmProvider.ExecuteAsync(
                prompt,
                new LlmRequestOptions { ExpectJson = true, UsageType = LlmUsageType.ObjectExtraction },
                ct);

            if (!response.IsSuccess || string.IsNullOrEmpty(response.Content))
            {
                logger.LogWarning("LLM returned empty response for price extraction");
                return null;
            }

            return ParseExtractionResponse(response.Content);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to extract price from HTML");
            return null;
        }
    }

    /// <summary>
    /// Processes a price check for a watch, storing history and evaluating alerts.
    /// </summary>
    public async Task<PriceCheckResult> ProcessPriceCheckAsync(
        WatchedSite watch,
        string html,
        CancellationToken ct = default)
    {
        var result = new PriceCheckResult { WatchId = watch.Id };

        // Extract current price/stock
        var extraction = await ExtractPriceAsync(html, ct: ct);
        if (extraction == null)
        {
            result.Success = false;
            result.Error = "Failed to extract price from page";
            return result;
        }

        result.CurrentPrice = extraction.Price?.Value;
        result.Currency = extraction.Price?.Currency;
        result.CurrentStockStatus = extraction.Stock?.Status;
        result.RawPriceText = extraction.Price?.RawText;
        result.RawStockText = extraction.Stock?.RawText;

        // Get previous values for comparison
        PriceHistoryEntry? previousEntry = null;
        if (extraction.Price?.Value != null)
        {
            previousEntry = await priceHistoryRepository.GetLatestAsync(
                watch.Id,
                "Price",
                ct: ct);
        }

        result.PreviousPrice = previousEntry?.Value;
        result.PreviousStockStatus = previousEntry?.StockStatus;

        // Store new entry
        if (extraction.Price?.Value != null)
        {
            var historyEntry = new PriceHistoryEntry
            {
                WatchId = watch.Id,
                FieldName = "Price",
                Value = extraction.Price.Value.Value,
                Currency = extraction.Price.Currency,
                StockStatus = extraction.Stock?.Status,
                RawPriceText = extraction.Price.RawText,
                RawStockText = extraction.Stock?.RawText,
                Timestamp = DateTime.UtcNow
            };

            await priceHistoryRepository.AddAsync(historyEntry, ct);
            result.HistoryEntryId = historyEntry.Id;
        }

        // Evaluate alerts if we have a schema with thresholds
        if (watch.Schema?.Fields != null)
        {
            var priceField = watch.Schema.Fields.FirstOrDefault(f =>
                f.Type == FieldType.Currency || f.Name.Contains("price", StringComparison.OrdinalIgnoreCase));

            if (priceField != null && extraction.Price?.Value != null)
            {
                var alertResult = alertEvaluator.Evaluate(
                    priceField,
                    previousEntry?.Value != null ? (double)previousEntry.Value : null,
                    (double)extraction.Price.Value.Value,
                    priceField.BaselineValue);

                result.AlertResult = alertResult;

                // Record triggers and send notifications
                if (alertResult.HasTriggeredAlerts)
                {
                    foreach (var triggered in alertResult.TriggeredThresholds)
                    {
                        alertEvaluator.RecordTrigger(triggered.Threshold);
                    }

                    // Calculate change metrics
                    double? changePercent = null;
                    double? changeAbsolute = null;
                    if (previousEntry?.Value != null && previousEntry.Value != 0)
                    {
                        changeAbsolute = (double)(extraction.Price.Value.Value - previousEntry.Value);
                        changePercent = changeAbsolute / (double)previousEntry.Value * 100;
                    }

                    var context = new NotificationContext
                    {
                        Watch = watch,
                        AlertResult = alertResult,
                        Field = priceField,
                        OldPrice = previousEntry?.Value,
                        NewPrice = extraction.Price.Value,
                        Currency = extraction.Price.Currency,
                        ChangePercent = changePercent,
                        ChangeAbsolute = changeAbsolute
                    };

                    await notificationService.SendAlertAsync(watch, alertResult, context, ct);
                }
            }

            // Evaluate stock status changes
            if (extraction.Stock?.Status != null && previousEntry?.StockStatus != null)
            {
                var stockField = watch.Schema.Fields.FirstOrDefault(f =>
                    f.Type == FieldType.Status || f.Name.Contains("stock", StringComparison.OrdinalIgnoreCase));

                if (stockField != null && extraction.Stock.Status != previousEntry.StockStatus)
                {
                    var stockAlertResult = alertEvaluator.EvaluateStockChange(
                        stockField,
                        previousEntry.StockStatus,
                        extraction.Stock.Status.Value);

                    if (stockAlertResult.HasTriggeredAlerts)
                    {
                        var context = new NotificationContext
                        {
                            Watch = watch,
                            AlertResult = stockAlertResult,
                            Field = stockField,
                            OldStockStatus = previousEntry.StockStatus,
                            NewStockStatus = extraction.Stock.Status
                        };

                        await notificationService.SendAlertAsync(watch, stockAlertResult, context, ct);
                    }
                }
            }
        }

        result.Success = true;
        return result;
    }

    /// <summary>
    /// Classifies stock status text using LLM.
    /// </summary>
    public async Task<StockStatus> ClassifyStockStatusAsync(string stockText, CancellationToken ct = default)
    {
        try
        {
            var prompt = PriceExtractionPrompts.BuildStockClassificationPrompt(stockText);
            var response = await llmProvider.ExecuteAsync(prompt, null, ct);

            if (!response.IsSuccess || string.IsNullOrEmpty(response.Content))
                return StockStatus.Unknown;

            var trimmed = response.Content.Trim();
            if (Enum.TryParse<StockStatus>(trimmed, ignoreCase: true, out var status))
                return status;

            // Try common variations
            return trimmed.ToLowerInvariant() switch
            {
                "in stock" or "instock" => StockStatus.InStock,
                "out of stock" or "outofstock" => StockStatus.OutOfStock,
                "limited stock" or "limitedstock" or "limited" => StockStatus.LimitedStock,
                "pre-order" or "preorder" => StockStatus.PreOrder,
                "discontinued" or "ended" or "ukončeno" => StockStatus.Discontinued,
                "backorder" or "back order" => StockStatus.Backorder,
                _ => StockStatus.Unknown
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to classify stock status: {StockText}", stockText);
            return StockStatus.Unknown;
        }
    }

    /// <summary>
    /// Parses price text using LLM for complex locale formats.
    /// </summary>
    public async Task<(decimal Value, string Currency)?> ParsePriceAsync(
        string priceText,
        string? locale = null,
        CancellationToken ct = default)
    {
        try
        {
            var prompt = PriceExtractionPrompts.BuildPriceParsingPrompt(priceText, locale);
            var response = await llmProvider.ExecuteAsync(prompt, new LlmRequestOptions { ExpectJson = true }, ct);

            if (!response.IsSuccess || string.IsNullOrEmpty(response.Content))
                return null;

            var json = ExtractJson(response.Content);
            if (json == null)
                return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("value", out var valueProp) &&
                root.TryGetProperty("currency", out var currencyProp))
            {
                var value = valueProp.GetDecimal();
                var currency = currencyProp.GetString() ?? "USD";
                return (value, currency);
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse price: {PriceText}", priceText);
            return null;
        }
    }

    private PriceExtractionResult? ParseExtractionResponse(string response)
    {
        try
        {
            var json = ExtractJson(response);
            if (json == null)
            {
                logger.LogWarning("Could not extract JSON from LLM response");
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var result = new PriceExtractionResult();

            // Parse price
            if (root.TryGetProperty("price", out var priceProp) && priceProp.ValueKind == JsonValueKind.Object)
            {
                result.Price = new ExtractedPrice
                {
                    Value = priceProp.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number
                        ? v.GetDecimal()
                        : null,
                    Currency = priceProp.TryGetProperty("currency", out var c)
                        ? c.GetString()
                        : null,
                    RawText = priceProp.TryGetProperty("rawText", out var r)
                        ? r.GetString()
                        : null
                };
            }

            // Parse stock
            if (root.TryGetProperty("stock", out var stockProp) && stockProp.ValueKind == JsonValueKind.Object)
            {
                StockStatus? status = null;
                if (stockProp.TryGetProperty("status", out var statusProp))
                {
                    var statusStr = statusProp.GetString();
                    if (Enum.TryParse<StockStatus>(statusStr, ignoreCase: true, out var parsed))
                        status = parsed;
                }

                result.Stock = new ExtractedStock
                {
                    Status = status,
                    RawText = stockProp.TryGetProperty("rawText", out var r) ? r.GetString() : null,
                    Quantity = stockProp.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number
                        ? q.GetInt32()
                        : null
                };
            }

            // Parse product name
            if (root.TryGetProperty("productName", out var nameProp))
            {
                result.ProductName = nameProp.GetString();
            }

            // Parse confidence
            if (root.TryGetProperty("confidence", out var confProp) && confProp.ValueKind == JsonValueKind.Number)
            {
                result.Confidence = (float)confProp.GetDouble();
            }

            return result;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse LLM response as JSON");
            return null;
        }
    }

    private static string? ExtractJson(string text)
    {
        // Find JSON object in response (may be wrapped in markdown code blocks)
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        if (start >= 0 && end > start)
        {
            return text[start..(end + 1)];
        }

        // Try array
        start = text.IndexOf('[');
        end = text.LastIndexOf(']');

        if (start >= 0 && end > start)
        {
            return text[start..(end + 1)];
        }

        return null;
    }
}

/// <summary>
/// Result of extracting price/stock from HTML.
/// </summary>
public class PriceExtractionResult
{
    public ExtractedPrice? Price { get; set; }
    public ExtractedStock? Stock { get; set; }
    public string? ProductName { get; set; }
    public float? Confidence { get; set; }
}

/// <summary>
/// Extracted price information.
/// </summary>
public class ExtractedPrice
{
    public decimal? Value { get; set; }
    public string? Currency { get; set; }
    public string? RawText { get; set; }
}

/// <summary>
/// Extracted stock information.
/// </summary>
public class ExtractedStock
{
    public StockStatus? Status { get; set; }
    public string? RawText { get; set; }
    public int? Quantity { get; set; }
}

