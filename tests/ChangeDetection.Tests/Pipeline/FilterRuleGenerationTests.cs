using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services;
using ChangeDetection.Services.Pipeline;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline;

/// <summary>
/// Tests for filter rule generation from pipeline session intent/keywords,
/// covering both the private BuildFilterRulesFromIntent logic (via reflection)
/// and integration with FilterEvaluationService.
/// </summary>
[Category("Unit")]
public class FilterRuleGenerationTests
{
    private static List<FilterRule> InvokeBuildFilterRulesFromIntent(PipelineSession session)
    {
        var method = typeof(WatchSetupPipeline).GetMethod(
            "BuildFilterRulesFromIntent",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.ShouldNotBeNull("BuildFilterRulesFromIntent method not found on WatchSetupPipeline");
        return (List<FilterRule>)method.Invoke(null, [session])!;
    }

    [Test]
    public async Task BuildFilterRules_WithSchemaAndKeywords_CreatesFieldBasedRules()
    {
        // Arrange
        var session = new PipelineSession
        {
            ContentAnalysis = new ContentAnalysis
            {
                FilterKeywords = ["Prague"]
            },
            SchemaEnabled = true,
            DiscoveredSchema = new DiscoveredSchema
            {
                ItemSelector = ".event",
                Fields =
                [
                    new DiscoveredField { Name = "city", Type = "string", Selector = ".city" },
                    new DiscoveredField { Name = "date", Type = "date", Selector = ".date" },
                    new DiscoveredField { Name = "venue", Type = "string", Selector = ".venue" }
                ]
            }
        };

        // Act
        var rules = InvokeBuildFilterRulesFromIntent(session);

        // Assert
        rules.Count.ShouldBe(1);
        var rule = rules[0];
        rule.Logic.ShouldBe(FilterLogic.Or);

        // Only text fields (city, venue) should have conditions — date is excluded
        rule.Conditions.Count.ShouldBe(2);
        rule.Conditions.ShouldAllBe(c => c.Operator == FilterOperator.Contains);
        rule.Conditions.ShouldAllBe(c => c.Value == "Prague");
        rule.Conditions.Select(c => c.FieldName).ShouldBe(["city", "venue"], ignoreOrder: true);

        rule.Actions.Count.ShouldBe(1);
        rule.Actions[0].Type.ShouldBe(FilterActionType.ImmediateNotify);

        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildFilterRules_WithoutKeywords_ReturnsEmptyList()
    {
        // Arrange
        var session = new PipelineSession
        {
            ContentAnalysis = new ContentAnalysis
            {
                FilterKeywords = []
            }
        };

        // Act
        var rules = InvokeBuildFilterRulesFromIntent(session);

        // Assert
        rules.ShouldBeEmpty();

        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildFilterRules_WithoutSchema_CreatesContentFilter()
    {
        // Arrange
        var session = new PipelineSession
        {
            ContentAnalysis = new ContentAnalysis
            {
                FilterKeywords = ["Prague"]
            },
            SchemaEnabled = false
        };

        // Act
        var rules = InvokeBuildFilterRulesFromIntent(session);

        // Assert
        rules.Count.ShouldBe(1);
        var rule = rules[0];
        rule.Conditions.Count.ShouldBe(1);
        rule.Conditions[0].FieldName.ShouldBe("$content");
        rule.Conditions[0].Operator.ShouldBe(FilterOperator.Contains);
        rule.Conditions[0].Value.ShouldBe("Prague");

        rule.Actions[0].Type.ShouldBe(FilterActionType.ImmediateNotify);

        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildFilterRules_MultipleKeywords_CreatesMultipleRules()
    {
        // Arrange
        var session = new PipelineSession
        {
            ContentAnalysis = new ContentAnalysis
            {
                FilterKeywords = ["Prague", "Brno"]
            },
            SchemaEnabled = true,
            DiscoveredSchema = new DiscoveredSchema
            {
                ItemSelector = ".event",
                Fields =
                [
                    new DiscoveredField { Name = "city", Type = "string", Selector = ".city" }
                ]
            }
        };

        // Act
        var rules = InvokeBuildFilterRulesFromIntent(session);

        // Assert
        rules.Count.ShouldBe(2);
        rules[0].Name.ShouldContain("Prague");
        rules[1].Name.ShouldContain("Brno");

        await Task.CompletedTask;
    }

    [Test]
    public async Task FilterRuleFromIntent_MatchesExtractedObject()
    {
        // Arrange — build filter rules the same way BuildFilterRulesFromIntent does
        var session = new PipelineSession
        {
            ContentAnalysis = new ContentAnalysis
            {
                FilterKeywords = ["Prague"]
            },
            SchemaEnabled = true,
            DiscoveredSchema = new DiscoveredSchema
            {
                ItemSelector = ".event",
                Fields =
                [
                    new DiscoveredField { Name = "city", Type = "string", Selector = ".city" },
                    new DiscoveredField { Name = "date", Type = "date", Selector = ".date" },
                    new DiscoveredField { Name = "venue", Type = "string", Selector = ".venue" }
                ]
            }
        };
        var rules = InvokeBuildFilterRulesFromIntent(session);

        var obj = new ExtractedObject
        {
            Fields = new Dictionary<string, string?>
            {
                ["city"] = "Prague",
                ["date"] = "2025-08-15",
                ["venue"] = "O2 Arena"
            }
        };

        var logger = Substitute.For<ILogger<FilterEvaluationService>>();
        var service = new FilterEvaluationService(logger);

        // Act
        var actions = await service.EvaluateObjectAsync(obj, ChangeType.Added, rules);

        // Assert
        actions.ShouldNotBeEmpty();
        actions.ShouldContain(a => a.Action.Type == FilterActionType.ImmediateNotify);
    }

    [Test]
    public async Task FilterRuleFromIntent_DoesNotMatchUnrelatedObject()
    {
        // Arrange
        var session = new PipelineSession
        {
            ContentAnalysis = new ContentAnalysis
            {
                FilterKeywords = ["Prague"]
            },
            SchemaEnabled = true,
            DiscoveredSchema = new DiscoveredSchema
            {
                ItemSelector = ".event",
                Fields =
                [
                    new DiscoveredField { Name = "city", Type = "string", Selector = ".city" },
                    new DiscoveredField { Name = "date", Type = "date", Selector = ".date" },
                    new DiscoveredField { Name = "venue", Type = "string", Selector = ".venue" }
                ]
            }
        };
        var rules = InvokeBuildFilterRulesFromIntent(session);

        var obj = new ExtractedObject
        {
            Fields = new Dictionary<string, string?>
            {
                ["city"] = "Berlin",
                ["date"] = "2025-08-20",
                ["venue"] = "Mercedes-Benz Arena"
            }
        };

        var logger = Substitute.For<ILogger<FilterEvaluationService>>();
        var service = new FilterEvaluationService(logger);

        // Act
        var actions = await service.EvaluateObjectAsync(obj, ChangeType.Added, rules);

        // Assert
        actions.ShouldBeEmpty();
    }
}
