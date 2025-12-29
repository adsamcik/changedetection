using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Content;

/// <summary>
/// Tests for FilterEvaluationService.
/// </summary>
public class FilterEvaluationServiceTests
{
    private readonly ILogger<FilterEvaluationService> _logger;
    private readonly FilterEvaluationService _sut;

    public FilterEvaluationServiceTests()
    {
        _logger = Substitute.For<ILogger<FilterEvaluationService>>();
        _sut = new FilterEvaluationService(_logger);
    }

    private static ExtractedObject CreateObject(string title, string? price = null)
    {
        var fields = new Dictionary<string, string?> { ["Title"] = title };
        if (price != null) fields["Price"] = price;
        return new ExtractedObject
        {
            IdentityKey = title,
            Fields = fields
        };
    }

    private static FilterRule CreateRule(
        string name,
        FilterOperator op,
        string fieldName,
        string? value,
        FilterActionType actionType,
        Dictionary<string, string>? actionParams = null)
    {
        return new FilterRule
        {
            Name = name,
            Conditions =
            [
                new FilterCondition
                {
                    FieldName = fieldName,
                    Operator = op,
                    Value = value
                }
            ],
            Logic = FilterLogic.And,
            Actions =
            [
                new FilterAction
                {
                    Type = actionType,
                    Parameters = actionParams ?? []
                }
            ],
            IsEnabled = true
        };
    }

    [Test]
    public async Task EvaluateAsync_WithNoRules_ReturnsEmptyResult()
    {
        // Arrange
        var diff = new ObjectDiffResult
        {
            AddedItems = [CreateObject("New Item")]
        };
        var rules = Array.Empty<FilterRule>();

        // Act
        var result = await _sut.EvaluateAsync(diff, rules);

        // Assert
        result.Actions.ShouldBeEmpty();
        result.FilteredObjects.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_WithContainsCondition_MatchesCorrectly()
    {
        // Arrange
        var diff = new ObjectDiffResult
        {
            AddedItems =
            [
                CreateObject("Workshop: AI Basics"),
                CreateObject("Conference: DevOps Summit")
            ]
        };

        var rules = new[]
        {
            CreateRule("Workshop Filter", FilterOperator.Contains, "Title", "Workshop",
                FilterActionType.AddTag, new Dictionary<string, string> { ["tag"] = "workshop" })
        };

        // Act
        var result = await _sut.EvaluateAsync(diff, rules);

        // Assert
        result.FilteredObjects.Count.ShouldBe(1);
        var title = result.FilteredObjects[0].Object.Fields["Title"];
        title.ShouldNotBeNull();
        title.ShouldContain("Workshop");
        result.TagsToAdd.ShouldContain("workshop");
    }

    [Test]
    public async Task EvaluateAsync_WithEqualsCondition_MatchesExactly()
    {
        // Arrange
        var diff = new ObjectDiffResult
        {
            AddedItems =
            [
                CreateObject("Exact Match"),
                CreateObject("Exact Match Plus More")
            ]
        };

        var rules = new[]
        {
            CreateRule("Exact Filter", FilterOperator.Equals, "Title", "Exact Match",
                FilterActionType.Highlight)
        };

        // Act
        var result = await _sut.EvaluateAsync(diff, rules);

        // Assert
        result.FilteredObjects.Count.ShouldBe(1);
        result.FilteredObjects[0].Object.Fields["Title"].ShouldBe("Exact Match");
    }

    [Test]
    public async Task EvaluateAsync_WithSuppressAction_SetsSuppressFlag()
    {
        // Arrange
        var diff = new ObjectDiffResult
        {
            AddedItems = [CreateObject("Boring Update")]
        };

        var rules = new[]
        {
            CreateRule("Suppress Boring", FilterOperator.Contains, "Title", "Boring",
                FilterActionType.SuppressNotification)
        };

        // Act
        var result = await _sut.EvaluateAsync(diff, rules);

        // Assert
        result.SuppressNotification.ShouldBeTrue();
    }

    [Test]
    public async Task EvaluateAsync_WithRouteAction_AddsChannel()
    {
        // Arrange
        var diff = new ObjectDiffResult
        {
            AddedItems = [CreateObject("Critical Alert")]
        };

        var rules = new[]
        {
            CreateRule("Route Critical", FilterOperator.Contains, "Title", "Critical",
                FilterActionType.RouteToChannel, new Dictionary<string, string> { ["channel"] = "alerts" })
        };

        // Act
        var result = await _sut.EvaluateAsync(diff, rules);

        // Assert
        result.RouteToChannels.ShouldContain("alerts");
    }

    [Test]
    public async Task EvaluateAsync_WithImportanceAction_SetsOverride()
    {
        // Arrange
        var diff = new ObjectDiffResult
        {
            AddedItems = [CreateObject("Important Event")]
        };

        var rules = new[]
        {
            CreateRule("High Priority", FilterOperator.Contains, "Title", "Important",
                FilterActionType.SetImportance, new Dictionary<string, string> { ["level"] = "High" })
        };

        // Act
        var result = await _sut.EvaluateAsync(diff, rules);

        // Assert
        result.ImportanceOverride.ShouldBe(ChangeImportance.High);
    }

    [Test]
    public async Task EvaluateAsync_WithDisabledRule_SkipsRule()
    {
        // Arrange
        var diff = new ObjectDiffResult
        {
            AddedItems = [CreateObject("Test Item")]
        };

        var rules = new[]
        {
            new FilterRule
            {
                Name = "Disabled Rule",
                Conditions = [new FilterCondition { FieldName = "Title", Operator = FilterOperator.Contains, Value = "Test" }],
                Actions = [new FilterAction { Type = FilterActionType.AddTag, Parameters = new Dictionary<string, string> { ["tag"] = "test" } }],
                IsEnabled = false
            }
        };

        // Act
        var result = await _sut.EvaluateAsync(diff, rules);

        // Assert
        result.FilteredObjects.ShouldBeEmpty();
        result.TagsToAdd.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_WithAmbiguousIdentities_SkipsEvaluation()
    {
        // Arrange
        var diff = new ObjectDiffResult
        {
            AddedItems = [CreateObject("Test Item")],
            HasAmbiguousIdentities = true
        };

        var rules = new[]
        {
            CreateRule("Test Rule", FilterOperator.Contains, "Title", "Test", FilterActionType.AddTag)
        };

        // Act
        var result = await _sut.EvaluateAsync(diff, rules);

        // Assert
        result.FilteredObjects.ShouldBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_WithMultipleMatchingRules_AppliesAll()
    {
        // Arrange
        var diff = new ObjectDiffResult
        {
            AddedItems = [CreateObject("AI Workshop")]
        };

        var rules = new[]
        {
            CreateRule("AI Tag", FilterOperator.Contains, "Title", "AI",
                FilterActionType.AddTag, new Dictionary<string, string> { ["tag"] = "ai" }),
            CreateRule("Workshop Tag", FilterOperator.Contains, "Title", "Workshop",
                FilterActionType.AddTag, new Dictionary<string, string> { ["tag"] = "workshop" })
        };

        // Act
        var result = await _sut.EvaluateAsync(diff, rules);

        // Assert
        result.TagsToAdd.ShouldContain("ai");
        result.TagsToAdd.ShouldContain("workshop");
    }

    [Test]
    public async Task EvaluateAsync_WithAndLogic_RequiresAllConditions()
    {
        // Arrange
        var diff = new ObjectDiffResult
        {
            AddedItems =
            [
                CreateObject("AI Workshop", "$100"),
                CreateObject("AI Seminar", "$50")
            ]
        };

        var rules = new[]
        {
            new FilterRule
            {
                Name = "Expensive AI Events",
                Conditions =
                [
                    new FilterCondition { FieldName = "Title", Operator = FilterOperator.Contains, Value = "AI" },
                    new FilterCondition { FieldName = "Price", Operator = FilterOperator.Contains, Value = "$100" }
                ],
                Logic = FilterLogic.And,
                Actions = [new FilterAction { Type = FilterActionType.Highlight }],
                IsEnabled = true
            }
        };

        // Act
        var result = await _sut.EvaluateAsync(diff, rules);

        // Assert
        result.FilteredObjects.Count.ShouldBe(1);
        result.FilteredObjects[0].Object.Fields["Title"].ShouldBe("AI Workshop");
    }

    [Test]
    public async Task EvaluateAsync_WithOrLogic_RequiresAnyCondition()
    {
        // Arrange
        var diff = new ObjectDiffResult
        {
            AddedItems =
            [
                CreateObject("AI Workshop"),
                CreateObject("ML Seminar"),
                CreateObject("Database Talk")
            ]
        };

        var rules = new[]
        {
            new FilterRule
            {
                Name = "AI or ML Events",
                Conditions =
                [
                    new FilterCondition { FieldName = "Title", Operator = FilterOperator.Contains, Value = "AI" },
                    new FilterCondition { FieldName = "Title", Operator = FilterOperator.Contains, Value = "ML" }
                ],
                Logic = FilterLogic.Or,
                Actions = [new FilterAction { Type = FilterActionType.AddTag, Parameters = new Dictionary<string, string> { ["tag"] = "tech" } }],
                IsEnabled = true
            }
        };

        // Act
        var result = await _sut.EvaluateAsync(diff, rules);

        // Assert
        result.FilteredObjects.Count.ShouldBe(2);
        result.TagsToAdd.ShouldContain("tech");
    }

    [Test]
    public async Task EvaluateAsync_WithChangeTypeCondition_FiltersCorrectly()
    {
        // Arrange
        var diff = new ObjectDiffResult
        {
            AddedItems = [CreateObject("New Item")],
            RemovedItems = [CreateObject("Old Item")]
        };

        var rules = new[]
        {
            CreateRule("Added Only", FilterOperator.Equals, "$changeType", "Added",
                FilterActionType.AddTag, new Dictionary<string, string> { ["tag"] = "new" })
        };

        // Act
        var result = await _sut.EvaluateAsync(diff, rules);

        // Assert
        result.FilteredObjects.Count.ShouldBe(1);
        result.FilteredObjects[0].Object.Fields["Title"].ShouldBe("New Item");
    }

    [Test]
    public async Task EvaluateAsync_WithRulesByPriority_EvaluatesInOrder()
    {
        // Arrange
        var diff = new ObjectDiffResult
        {
            AddedItems = [CreateObject("Test Item")]
        };

        var rules = new[]
        {
            new FilterRule
            {
                Name = "Low Priority",
                Priority = 1,
                Conditions = [new FilterCondition { FieldName = "Title", Operator = FilterOperator.Contains, Value = "Test" }],
                Actions = [new FilterAction { Type = FilterActionType.SetImportance, Parameters = new Dictionary<string, string> { ["level"] = "Low" } }],
                IsEnabled = true
            },
            new FilterRule
            {
                Name = "High Priority",
                Priority = 10,
                Conditions = [new FilterCondition { FieldName = "Title", Operator = FilterOperator.Contains, Value = "Test" }],
                Actions = [new FilterAction { Type = FilterActionType.SetImportance, Parameters = new Dictionary<string, string> { ["level"] = "High" } }],
                IsEnabled = true
            }
        };

        // Act
        var result = await _sut.EvaluateAsync(diff, rules);

        // Assert
        // The highest importance should win
        result.ImportanceOverride.ShouldBe(ChangeImportance.High);
    }

    [Test]
    public async Task EvaluateAsync_WithRequireReviewAction_AddsToReviewList()
    {
        // Arrange
        var diff = new ObjectDiffResult
        {
            AddedItems = [CreateObject("Review This")]
        };

        var rules = new[]
        {
            CreateRule("Review Required", FilterOperator.Contains, "Title", "Review",
                FilterActionType.RequireReview)
        };

        // Act
        var result = await _sut.EvaluateAsync(diff, rules);

        // Assert
        result.ObjectsRequiringReview.ShouldNotBeEmpty();
    }

    [Test]
    public async Task EvaluateAsync_WithIsEmptyCondition_MatchesEmptyFields()
    {
        // Arrange
        var obj = new ExtractedObject
        {
            IdentityKey = "Test",
            Fields = new Dictionary<string, string?>
            {
                ["Title"] = "Test",
                ["Description"] = null
            }
        };

        var diff = new ObjectDiffResult
        {
            AddedItems = [obj]
        };

        var rules = new[]
        {
            CreateRule("Missing Description", FilterOperator.IsEmpty, "Description", null,
                FilterActionType.AddTag, new Dictionary<string, string> { ["tag"] = "incomplete" })
        };

        // Act
        var result = await _sut.EvaluateAsync(diff, rules);

        // Assert
        result.TagsToAdd.ShouldContain("incomplete");
    }

    [Test]
    public async Task EvaluateAsync_AppliesActionsToAllMatchingChangeTypes()
    {
        // Arrange
        var diff = new ObjectDiffResult
        {
            AddedItems = [CreateObject("Added Event")],
            RemovedItems = [CreateObject("Removed Event")],
            ModifiedItems =
            [
                new ObjectModification
                {
                    IdentityKey = "Modified Event",
                    PreviousObject = CreateObject("Modified Event", "$10"),
                    CurrentObject = CreateObject("Modified Event", "$20"),
                    FieldChanges = [new FieldChange { FieldName = "Price", OldValue = "$10", NewValue = "$20" }]
                }
            ]
        };

        var rules = new[]
        {
            CreateRule("All Events", FilterOperator.Contains, "Title", "Event",
                FilterActionType.AddTag, new Dictionary<string, string> { ["tag"] = "event" })
        };

        // Act
        var result = await _sut.EvaluateAsync(diff, rules);

        // Assert
        result.FilteredObjects.Count.ShouldBe(3);
    }
}
