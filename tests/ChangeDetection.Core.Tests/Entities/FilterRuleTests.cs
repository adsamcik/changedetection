using ChangeDetection.Core.Entities;
using Shouldly;

namespace ChangeDetection.Core.Tests.Entities;

[Category("Unit")]
public class FilterRuleTests
{
    [Test]
    public async Task DefaultValues_ShouldBeCorrect()
    {
        var rule = new FilterRule { Name = "Test Rule" };

        rule.Id.ShouldNotBe(Guid.Empty);
        rule.IsEnabled.ShouldBeTrue();
        rule.Logic.ShouldBe(FilterLogic.And);
        rule.Priority.ShouldBe(0);
        rule.StopProcessing.ShouldBeFalse();
        rule.Description.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Name_ShouldBeSet()
    {
        var rule = new FilterRule { Name = "Price Drop Alert" };

        rule.Name.ShouldBe("Price Drop Alert");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Collections_ShouldDefaultToEmpty()
    {
        var rule = new FilterRule { Name = "Test Rule" };

        rule.Conditions.ShouldBeEmpty();
        rule.Actions.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task FilterCondition_DefaultValues_ShouldBeCorrect()
    {
        var condition = new FilterCondition { FieldName = "price" };

        condition.Operator.ShouldBe(FilterOperator.Contains);
        condition.Negate.ShouldBeFalse();
        condition.Value.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task FilterAction_Parameters_ShouldDefaultToEmptyDictionary()
    {
        var action = new FilterAction();

        action.Parameters.ShouldNotBeNull();
        action.Parameters.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Id_ShouldBeUnique()
    {
        var rule1 = new FilterRule { Name = "Rule 1" };
        var rule2 = new FilterRule { Name = "Rule 2" };

        rule1.Id.ShouldNotBe(rule2.Id);
        await Task.CompletedTask;
    }
}
