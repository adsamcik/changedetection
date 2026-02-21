using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

/// <summary>
/// Unit tests for PriceTrackingService.
/// Tests LLM-based extraction, price history storage, alert evaluation, and notifications.
/// </summary>
[Category("Unit")]
public class PriceTrackingServiceTests
{
    private readonly ILlmProviderChain _llmProvider;
    private readonly IPriceHistoryRepository _priceHistoryRepo;
    private readonly IAlertThresholdEvaluator _alertEvaluator;
    private readonly INotificationService _notificationService;
    private readonly ILogger<PriceTrackingService> _logger;
    private readonly PriceTrackingService _sut;

    public PriceTrackingServiceTests()
    {
        _llmProvider = Substitute.For<ILlmProviderChain>();
        _priceHistoryRepo = Substitute.For<IPriceHistoryRepository>();
        _alertEvaluator = Substitute.For<IAlertThresholdEvaluator>();
        _notificationService = Substitute.For<INotificationService>();
        _logger = Substitute.For<ILogger<PriceTrackingService>>();
        _sut = new PriceTrackingService(
            _llmProvider,
            _priceHistoryRepo,
            _alertEvaluator,
            _notificationService,
            _logger);
    }

    #region ExtractPriceAsync Tests

    [Test]
    public async Task ExtractPriceAsync_ValidHtml_ExtractsPriceAndStock()
    {
        // Arrange
        var json = """{"price":{"value":29.99,"currency":"USD","rawText":"$29.99"},"stock":{"status":"InStock","rawText":"In Stock"},"productName":"Widget","confidence":0.95}""";
        _llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = json });

        // Act
        var result = await _sut.ExtractPriceAsync("<html><body>$29.99 In Stock</body></html>");

        // Assert
        result.ShouldNotBeNull();
        result.Price.ShouldNotBeNull();
        result.Price.Value.ShouldBe(29.99m);
        result.Price.Currency.ShouldBe("USD");
        result.Price.RawText.ShouldBe("$29.99");
        result.Stock.ShouldNotBeNull();
        result.Stock.Status.ShouldBe(StockStatus.InStock);
        result.Stock.RawText.ShouldBe("In Stock");
        result.ProductName.ShouldBe("Widget");
        result.Confidence.ShouldBe(0.95f);
    }

    [Test]
    public async Task ExtractPriceAsync_NoPrice_ReturnsNull()
    {
        // Arrange — LLM returns success but empty content
        _llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = "" });

        // Act
        var result = await _sut.ExtractPriceAsync("<html><body>No price here</body></html>");

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public async Task ExtractPriceAsync_LlmFailure_ReturnsNull()
    {
        // Arrange — LLM throws exception
        _llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("LLM provider unavailable"));

        // Act
        var result = await _sut.ExtractPriceAsync("<html><body>$10.00</body></html>");

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public async Task ExtractPriceAsync_LlmReturnsNotSuccess_ReturnsNull()
    {
        // Arrange
        _llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = false, ErrorMessage = "Rate limited" });

        // Act
        var result = await _sut.ExtractPriceAsync("<html><body>$10.00</body></html>");

        // Assert
        result.ShouldBeNull();
    }

    #endregion

    #region ProcessPriceCheckAsync Tests

    [Test]
    public async Task ProcessPriceCheckAsync_NewPrice_StoresHistory()
    {
        // Arrange
        var watch = CreateWatch();
        var json = """{"price":{"value":49.99,"currency":"EUR","rawText":"€49.99"}}""";
        SetupLlmResponse(json);

        // No previous entry
        _priceHistoryRepo.GetLatestAsync(watch.Id, "Price", null, Arg.Any<CancellationToken>())
            .Returns((PriceHistoryEntry?)null);

        // Act
        var result = await _sut.ProcessPriceCheckAsync(watch, "<html>€49.99</html>");

        // Assert
        result.Success.ShouldBeTrue();
        result.CurrentPrice.ShouldBe(49.99m);
        result.Currency.ShouldBe("EUR");
        await _priceHistoryRepo.Received(1).AddAsync(
            Arg.Is<PriceHistoryEntry>(e =>
                e.WatchId == watch.Id &&
                e.FieldName == "Price" &&
                e.Value == 49.99m &&
                e.Currency == "EUR"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessPriceCheckAsync_PriceChanged_DetectsChange()
    {
        // Arrange
        var watch = CreateWatch();
        var json = """{"price":{"value":79.99,"currency":"USD","rawText":"$79.99"}}""";
        SetupLlmResponse(json);

        var previousEntry = new PriceHistoryEntry
        {
            WatchId = watch.Id,
            FieldName = "Price",
            Value = 99.99m,
            Currency = "USD",
            Timestamp = DateTime.UtcNow.AddHours(-1)
        };
        _priceHistoryRepo.GetLatestAsync(watch.Id, "Price", null, Arg.Any<CancellationToken>())
            .Returns(previousEntry);

        // Act
        var result = await _sut.ProcessPriceCheckAsync(watch, "<html>$79.99</html>");

        // Assert
        result.Success.ShouldBeTrue();
        result.CurrentPrice.ShouldBe(79.99m);
        result.PreviousPrice.ShouldBe(99.99m);
        result.HasPriceChange.ShouldBeTrue();
        result.ChangeAbsolute.ShouldBe(-20.00m);
    }

    [Test]
    public async Task ProcessPriceCheckAsync_ThresholdCrossed_SendsNotification()
    {
        // Arrange
        var threshold = new FieldAlertThreshold
        {
            Id = Guid.NewGuid(),
            ConditionType = AlertConditionType.DropsBelow,
            Value = 50,
            IsEnabled = true
        };
        var priceField = CreatePriceField(threshold);
        var watch = CreateWatch(priceField);
        var json = """{"price":{"value":39.99,"currency":"USD","rawText":"$39.99"}}""";
        SetupLlmResponse(json);

        var previousEntry = new PriceHistoryEntry
        {
            WatchId = watch.Id,
            FieldName = "Price",
            Value = 59.99m,
            Currency = "USD",
            Timestamp = DateTime.UtcNow.AddHours(-1)
        };
        _priceHistoryRepo.GetLatestAsync(watch.Id, "Price", null, Arg.Any<CancellationToken>())
            .Returns(previousEntry);

        var triggeredResult = new AlertEvaluationResult
        {
            TriggeredThresholds =
            [
                new TriggeredThreshold
                {
                    Threshold = threshold,
                    Field = priceField,
                    Message = "Price dropped below 50",
                    OldValue = 59.99,
                    NewValue = 39.99
                }
            ],
            HighestImportance = ChangeImportance.High,
            CombinedMessage = "Price dropped below 50"
        };

        _alertEvaluator.Evaluate(
            Arg.Any<SchemaField>(),
            Arg.Any<double?>(),
            Arg.Any<double>(),
            Arg.Any<double?>())
            .Returns(triggeredResult);

        // Act
        var result = await _sut.ProcessPriceCheckAsync(watch, "<html>$39.99</html>");

        // Assert
        result.Success.ShouldBeTrue();
        result.AlertResult.ShouldNotBeNull();
        result.AlertResult.HasTriggeredAlerts.ShouldBeTrue();
        _alertEvaluator.Received(1).RecordTrigger(threshold);
        await _notificationService.Received(1).SendAlertAsync(
            watch,
            triggeredResult,
            Arg.Any<NotificationContext>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessPriceCheckAsync_StockStatusChanged_DetectsChange()
    {
        // Arrange
        var stockField = new SchemaField
        {
            Name = "stock",
            Type = FieldType.Status,
            Selector = ".stock"
        };
        var watch = CreateWatch(stockField: stockField);
        var json = """{"price":{"value":29.99,"currency":"USD","rawText":"$29.99"},"stock":{"status":"OutOfStock","rawText":"Out of Stock"}}""";
        SetupLlmResponse(json);

        var previousEntry = new PriceHistoryEntry
        {
            WatchId = watch.Id,
            FieldName = "Price",
            Value = 29.99m,
            StockStatus = StockStatus.InStock,
            Timestamp = DateTime.UtcNow.AddHours(-1)
        };
        _priceHistoryRepo.GetLatestAsync(watch.Id, "Price", null, Arg.Any<CancellationToken>())
            .Returns(previousEntry);

        var stockAlertResult = new AlertEvaluationResult
        {
            TriggeredThresholds = [],
            HighestImportance = null,
            CombinedMessage = null
        };

        _alertEvaluator.EvaluateStockChange(
            Arg.Any<SchemaField>(),
            Arg.Any<StockStatus?>(),
            Arg.Any<StockStatus>())
            .Returns(stockAlertResult);

        // Act
        var result = await _sut.ProcessPriceCheckAsync(watch, "<html>$29.99 Out of Stock</html>");

        // Assert
        result.Success.ShouldBeTrue();
        result.CurrentStockStatus.ShouldBe(StockStatus.OutOfStock);
        result.PreviousStockStatus.ShouldBe(StockStatus.InStock);
        result.HasStockChange.ShouldBeTrue();
    }

    [Test]
    public async Task ProcessPriceCheckAsync_ExtractionFails_ReturnsFailure()
    {
        // Arrange
        var watch = CreateWatch();
        _llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = false });

        // Act
        var result = await _sut.ProcessPriceCheckAsync(watch, "<html>no price</html>");

        // Assert
        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
        await _priceHistoryRepo.DidNotReceive().AddAsync(
            Arg.Any<PriceHistoryEntry>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region ClassifyStockStatusAsync Tests

    [Test]
    public async Task ClassifyStockStatusAsync_InStockText_ReturnsInStock()
    {
        // Arrange
        _llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = "InStock" });

        // Act
        var result = await _sut.ClassifyStockStatusAsync("In Stock");

        // Assert
        result.ShouldBe(StockStatus.InStock);
    }

    [Test]
    public async Task ClassifyStockStatusAsync_OutOfStockText_ReturnsOutOfStock()
    {
        // Arrange
        _llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = "OutOfStock" });

        // Act
        var result = await _sut.ClassifyStockStatusAsync("Out of Stock");

        // Assert
        result.ShouldBe(StockStatus.OutOfStock);
    }

    [Test]
    public async Task ClassifyStockStatusAsync_LlmFails_ReturnsUnknown()
    {
        // Arrange
        _llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("LLM failure"));

        // Act
        var result = await _sut.ClassifyStockStatusAsync("Some stock text");

        // Assert
        result.ShouldBe(StockStatus.Unknown);
    }

    #endregion

    #region ParsePriceAsync Tests

    [Test]
    public async Task ParsePriceAsync_StandardFormat_ParsesCorrectly()
    {
        // Arrange
        var json = """{"value":29.99,"currency":"USD"}""";
        _llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = json });

        // Act
        var result = await _sut.ParsePriceAsync("$29.99");

        // Assert
        result.ShouldNotBeNull();
        result.Value.Value.ShouldBe(29.99m);
        result.Value.Currency.ShouldBe("USD");
    }

    [Test]
    public async Task ParsePriceAsync_InvalidFormat_ReturnsNull()
    {
        // Arrange — LLM returns empty for invalid input
        _llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = "not json" });

        // Act
        var result = await _sut.ParsePriceAsync("not a price");

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public async Task ParsePriceAsync_ZeroPrice_HandlesGracefully()
    {
        // Arrange
        var json = """{"value":0.00,"currency":"USD"}""";
        _llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = json });

        // Act
        var result = await _sut.ParsePriceAsync("$0.00");

        // Assert
        result.ShouldNotBeNull();
        result.Value.Value.ShouldBe(0.00m);
        result.Value.Currency.ShouldBe("USD");
    }

    [Test]
    public async Task ParsePriceAsync_LlmReturnsFailure_ReturnsNull()
    {
        // Arrange
        _llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = false });

        // Act
        var result = await _sut.ParsePriceAsync("$10.00");

        // Assert
        result.ShouldBeNull();
    }

    #endregion

    #region Helpers

    private static WatchedSite CreateWatch(
        SchemaField? priceField = null,
        SchemaField? stockField = null)
    {
        var fields = new List<SchemaField>();
        if (priceField != null) fields.Add(priceField);
        if (stockField != null) fields.Add(stockField);

        return new WatchedSite
        {
            Id = Guid.NewGuid(),
            Url = "https://example.com/product",
            Name = "Test Product",
            Schema = fields.Count > 0
                ? new ExtractionSchema
                {
                    ItemSelector = ".product",
                    Fields = fields
                }
                : null
        };
    }

    private static SchemaField CreatePriceField(FieldAlertThreshold? threshold = null)
    {
        var field = new SchemaField
        {
            Name = "price",
            Type = FieldType.Currency,
            Selector = ".price"
        };

        if (threshold != null)
        {
            field.AlertThresholds = [threshold];
        }

        return field;
    }

    private void SetupLlmResponse(string jsonContent)
    {
        _llmProvider.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = jsonContent });
    }

    #endregion
}
