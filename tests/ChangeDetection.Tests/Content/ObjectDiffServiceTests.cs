using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Content;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Content;

/// <summary>
/// Tests for ObjectDiffService.
/// </summary>
[Category("Unit")]
public class ObjectDiffServiceTests
{
    private readonly ILlmProviderChain _llmChain;
    private readonly ILogger<ObjectDiffService> _logger;
    private readonly ObjectDiffService _sut;

    public ObjectDiffServiceTests()
    {
        _llmChain = Substitute.For<ILlmProviderChain>();
        _logger = Substitute.For<ILogger<ObjectDiffService>>();
        _sut = new ObjectDiffService(_llmChain, _logger);
    }

    private static ExtractionSchema CreateTestSchema(DiffGranularity granularity = DiffGranularity.Both)
    {
        return new ExtractionSchema
        {
            ItemSelector = ".item",
            Fields =
            [
                new SchemaField { Name = "Title", Selector = ".title", IsIdentityField = true },
                new SchemaField { Name = "Price", Selector = ".price" }
            ],
            IdentityFieldNames = ["Title"],
            DiffSettings = new ObjectDiffSettings { Granularity = granularity }
        };
    }

    private static ExtractedObject CreateObject(string title, string price)
    {
        return new ExtractedObject
        {
            IdentityKey = title,
            Fields = new Dictionary<string, string?>
            {
                ["Title"] = title,
                ["Price"] = price
            }
        };
    }

    [Test]
    public async Task ComputeDiffAsync_DetectsAddedItems()
    {
        // Arrange
        var schema = CreateTestSchema();
        var previous = new List<ExtractedObject>
        {
            CreateObject("Item A", "$10")
        };
        var current = new List<ExtractedObject>
        {
            CreateObject("Item A", "$10"),
            CreateObject("Item B", "$20")
        };

        // Act
        var result = await _sut.ComputeDiffAsync(previous, current, schema);

        // Assert
        result.AddedItems.Count.ShouldBe(1);
        result.AddedItems[0].IdentityKey.ShouldBe("Item B");
        result.RemovedItems.ShouldBeEmpty();
        result.HasChanges.ShouldBeTrue();
    }

    [Test]
    public async Task ComputeDiffAsync_DetectsRemovedItems()
    {
        // Arrange
        var schema = CreateTestSchema();
        var previous = new List<ExtractedObject>
        {
            CreateObject("Item A", "$10"),
            CreateObject("Item B", "$20")
        };
        var current = new List<ExtractedObject>
        {
            CreateObject("Item A", "$10")
        };

        // Act
        var result = await _sut.ComputeDiffAsync(previous, current, schema);

        // Assert
        result.RemovedItems.Count.ShouldBe(1);
        result.RemovedItems[0].IdentityKey.ShouldBe("Item B");
        result.AddedItems.ShouldBeEmpty();
        result.HasChanges.ShouldBeTrue();
    }

    [Test]
    public async Task ComputeDiffAsync_DetectsModifiedItems()
    {
        // Arrange
        var schema = CreateTestSchema();
        var previous = new List<ExtractedObject>
        {
            CreateObject("Item A", "$10")
        };
        var current = new List<ExtractedObject>
        {
            CreateObject("Item A", "$15")  // Price changed
        };

        // Act
        var result = await _sut.ComputeDiffAsync(previous, current, schema);

        // Assert
        result.ModifiedItems.Count.ShouldBe(1);
        result.ModifiedItems[0].IdentityKey.ShouldBe("Item A");
        result.ModifiedItems[0].FieldChanges.ShouldContain(fc => fc.FieldName == "Price");
        result.AddedItems.ShouldBeEmpty();
        result.RemovedItems.ShouldBeEmpty();
        result.HasChanges.ShouldBeTrue();
    }

    [Test]
    public async Task ComputeDiffAsync_WithItemsOnlyGranularity_IgnoresFieldChanges()
    {
        // Arrange
        var schema = CreateTestSchema(DiffGranularity.ItemsOnly);
        var previous = new List<ExtractedObject>
        {
            CreateObject("Item A", "$10")
        };
        var current = new List<ExtractedObject>
        {
            CreateObject("Item A", "$15")  // Price changed but should be ignored
        };

        // Act
        var result = await _sut.ComputeDiffAsync(previous, current, schema);

        // Assert
        result.ModifiedItems.ShouldBeEmpty();
        result.HasChanges.ShouldBeFalse();
    }

    [Test]
    public async Task ComputeDiffAsync_WithFieldLevelGranularity_IgnoresAddedRemoved()
    {
        // Arrange
        var schema = CreateTestSchema(DiffGranularity.FieldLevel);
        var previous = new List<ExtractedObject>
        {
            CreateObject("Item A", "$10")
        };
        var current = new List<ExtractedObject>
        {
            CreateObject("Item A", "$10"),
            CreateObject("Item B", "$20")  // Added but should be ignored
        };

        // Act
        var result = await _sut.ComputeDiffAsync(previous, current, schema);

        // Assert
        result.AddedItems.ShouldBeEmpty();
        result.RemovedItems.ShouldBeEmpty();
        result.HasChanges.ShouldBeFalse();
    }

    [Test]
    public async Task ComputeDiffAsync_DetectsAmbiguousIdentities()
    {
        // Arrange
        var schema = CreateTestSchema();
        var previous = new List<ExtractedObject>
        {
            CreateObject("Duplicate", "$10"),
            CreateObject("Duplicate", "$20")
        };
        var current = new List<ExtractedObject>
        {
            CreateObject("Duplicate", "$15")
        };

        // Act
        var result = await _sut.ComputeDiffAsync(previous, current, schema);

        // Assert
        result.HasAmbiguousIdentities.ShouldBeTrue();
        result.AmbiguityDetails.ShouldNotBeEmpty();
    }

    [Test]
    public async Task ComputeDiffAsync_WithNoChanges_ReportsNoChanges()
    {
        // Arrange
        var schema = CreateTestSchema();
        var previous = new List<ExtractedObject>
        {
            CreateObject("Item A", "$10"),
            CreateObject("Item B", "$20")
        };
        var current = new List<ExtractedObject>
        {
            CreateObject("Item A", "$10"),
            CreateObject("Item B", "$20")
        };

        // Act
        var result = await _sut.ComputeDiffAsync(previous, current, schema);

        // Assert
        result.HasChanges.ShouldBeFalse();
        result.AddedItems.ShouldBeEmpty();
        result.RemovedItems.ShouldBeEmpty();
        result.ModifiedItems.ShouldBeEmpty();
    }

    [Test]
    public async Task ComputeDiffAsync_TracksFieldChangeDetails()
    {
        // Arrange
        var schema = CreateTestSchema();
        var previous = new List<ExtractedObject>
        {
            CreateObject("Product", "$100")
        };
        var current = new List<ExtractedObject>
        {
            CreateObject("Product", "$75")
        };

        // Act
        var result = await _sut.ComputeDiffAsync(previous, current, schema);

        // Assert
        result.ModifiedItems.Count.ShouldBe(1);
        var modification = result.ModifiedItems[0];
        modification.PreviousObject.Fields["Price"].ShouldBe("$100");
        modification.CurrentObject.Fields["Price"].ShouldBe("$75");
        
        var priceChange = modification.FieldChanges.First(fc => fc.FieldName == "Price");
        priceChange.OldValue.ShouldBe("$100");
        priceChange.NewValue.ShouldBe("$75");
    }
}
