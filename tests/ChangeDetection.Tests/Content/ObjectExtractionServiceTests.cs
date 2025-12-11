using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Content;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace ChangeDetection.Tests.Content;

/// <summary>
/// Tests for ObjectExtractionService.
/// </summary>
public class ObjectExtractionServiceTests
{
    private readonly ILlmProviderChain _llmChain;
    private readonly ILogger<ObjectExtractionService> _logger;
    private readonly ObjectExtractionService _sut;

    public ObjectExtractionServiceTests()
    {
        _llmChain = Substitute.For<ILlmProviderChain>();
        _logger = Substitute.For<ILogger<ObjectExtractionService>>();
        _sut = new ObjectExtractionService(_llmChain, _logger);
    }

    [Fact]
    public async Task ExtractAsync_WithNoItemsFound_ReturnsDriftDetected()
    {
        // Arrange
        var html = "<html><body><p>No items here</p></body></html>";
        var schema = new ExtractionSchema
        {
            ItemSelector = ".event-card",
            Fields =
            [
                new SchemaField { Name = "Title", Selector = ".title", IsRequired = true }
            ]
        };

        // Act
        var result = await _sut.ExtractAsync(html, schema);

        // Assert
        result.Success.ShouldBeFalse();
        result.DriftDetected.ShouldBeTrue();
        result.Error.ShouldContain("No items found");
    }

    [Fact]
    public async Task ExtractAsync_WithValidSchema_ExtractsObjects()
    {
        // Arrange
        var html = """
            <html><body>
                <div class="event-card">
                    <h2 class="title">Event 1</h2>
                    <span class="date">2024-01-15</span>
                </div>
                <div class="event-card">
                    <h2 class="title">Event 2</h2>
                    <span class="date">2024-01-20</span>
                </div>
            </body></html>
            """;

        var schema = new ExtractionSchema
        {
            ItemSelector = ".event-card",
            Fields =
            [
                new SchemaField { Name = "Title", Selector = ".title", IsRequired = true, IsIdentityField = true },
                new SchemaField { Name = "Date", Selector = ".date", Type = FieldType.Date }
            ],
            IdentityFieldNames = ["Title"]
        };

        // Mock LLM to return extracted objects
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                    [
                        {"Title": "Event 1", "Date": "2024-01-15"},
                        {"Title": "Event 2", "Date": "2024-01-20"}
                    ]
                    """
            });

        // Act
        var result = await _sut.ExtractAsync(html, schema);

        // Assert
        result.Success.ShouldBeTrue();
        result.Objects.ShouldNotBeNull();
        result.Objects.Count.ShouldBe(2);
        result.Objects[0].Fields["Title"].ShouldBe("Event 1");
        result.Objects[1].Fields["Title"].ShouldBe("Event 2");
    }

    [Fact]
    public async Task ExtractAsync_WithDuplicateIdentities_ReturnsWarnings()
    {
        // Arrange
        var html = """
            <html><body>
                <div class="event-card">
                    <h2 class="title">Duplicate Event</h2>
                </div>
                <div class="event-card">
                    <h2 class="title">Duplicate Event</h2>
                </div>
            </body></html>
            """;

        var schema = new ExtractionSchema
        {
            ItemSelector = ".event-card",
            Fields =
            [
                new SchemaField { Name = "Title", Selector = ".title", IsIdentityField = true }
            ],
            IdentityFieldNames = ["Title"]
        };

        // Mock LLM to return objects with same title
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                    [
                        {"Title": "Duplicate Event"},
                        {"Title": "Duplicate Event"}
                    ]
                    """
            });

        // Act
        var result = await _sut.ExtractAsync(html, schema);

        // Assert
        result.Success.ShouldBeTrue();
        result.AmbiguousIdentityWarnings.ShouldNotBeEmpty();
        result.AmbiguousIdentityWarnings[0].ShouldContain("Duplicate Event");
    }

    [Fact]
    public async Task ExtractAsync_ComputesIdentityKey()
    {
        // Arrange
        var html = """
            <html><body>
                <div class="product">
                    <span class="name">Widget</span>
                    <span class="sku">SKU-001</span>
                </div>
            </body></html>
            """;

        var schema = new ExtractionSchema
        {
            ItemSelector = ".product",
            Fields =
            [
                new SchemaField { Name = "Name", Selector = ".name", IsIdentityField = true },
                new SchemaField { Name = "SKU", Selector = ".sku", IsIdentityField = true }
            ],
            IdentityFieldNames = ["Name", "SKU"]
        };

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """[{"Name": "Widget", "SKU": "SKU-001"}]"""
            });

        // Act
        var result = await _sut.ExtractAsync(html, schema);

        // Assert
        result.Success.ShouldBeTrue();
        result.Objects.ShouldNotBeNull();
        result.Objects[0].IdentityKey.ShouldNotBeNullOrEmpty();
        result.Objects[0].IdentityKey.ShouldContain("Widget");
        result.Objects[0].IdentityKey.ShouldContain("SKU-001");
    }

    [Fact]
    public async Task ExtractAsync_SetsObjectIndex()
    {
        // Arrange
        var html = """
            <html><body>
                <div class="item"><span class="name">A</span></div>
                <div class="item"><span class="name">B</span></div>
                <div class="item"><span class="name">C</span></div>
            </body></html>
            """;

        var schema = new ExtractionSchema
        {
            ItemSelector = ".item",
            Fields = [new SchemaField { Name = "Name", Selector = ".name" }]
        };

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """[{"Name": "A"}, {"Name": "B"}, {"Name": "C"}]"""
            });

        // Act
        var result = await _sut.ExtractAsync(html, schema);

        // Assert
        result.Success.ShouldBeTrue();
        result.Objects![0].Index.ShouldBe(0);
        result.Objects![1].Index.ShouldBe(1);
        result.Objects![2].Index.ShouldBe(2);
    }
}
