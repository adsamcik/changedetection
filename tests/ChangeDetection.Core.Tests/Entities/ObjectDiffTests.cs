using ChangeDetection.Core.Entities;
using Shouldly;

namespace ChangeDetection.Core.Tests.Entities;

[Category("Unit")]
public class ObjectDiffTests
{
    [Test]
    public async Task HasChanges_ShouldBeFalse_WhenAllEmpty()
    {
        var result = new ObjectDiffResult();

        result.HasChanges.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task HasChanges_ShouldBeTrue_WhenItemsAdded()
    {
        var result = new ObjectDiffResult
        {
            AddedItems = [new ExtractedObject()]
        };

        result.HasChanges.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task HasChanges_ShouldBeTrue_WhenItemsRemoved()
    {
        var result = new ObjectDiffResult
        {
            RemovedItems = [new ExtractedObject()]
        };

        result.HasChanges.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task HasChanges_ShouldBeTrue_WhenItemsModified()
    {
        var result = new ObjectDiffResult
        {
            ModifiedItems =
            [
                new ObjectModification
                {
                    IdentityKey = "key1",
                    PreviousObject = new ExtractedObject(),
                    CurrentObject = new ExtractedObject()
                }
            ]
        };

        result.HasChanges.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ObjectDiffResult_Collections_ShouldDefaultToEmpty()
    {
        var result = new ObjectDiffResult();

        result.AddedItems.ShouldBeEmpty();
        result.RemovedItems.ShouldBeEmpty();
        result.ModifiedItems.ShouldBeEmpty();
        result.AmbiguityDetails.ShouldBeEmpty();
        result.HasAmbiguousIdentities.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    [Arguments(FieldType.Number, true)]
    [Arguments(FieldType.Currency, true)]
    [Arguments(FieldType.Percentage, true)]
    [Arguments(FieldType.String, false)]
    [Arguments(FieldType.Date, false)]
    [Arguments(FieldType.Url, false)]
    [Arguments(FieldType.Image, false)]
    [Arguments(FieldType.Html, false)]
    [Arguments(FieldType.Duration, false)]
    [Arguments(FieldType.Boolean, false)]
    [Arguments(FieldType.Status, false)]
    public async Task FieldChange_IsNumeric_ShouldReturnCorrectValue(FieldType fieldType, bool expected)
    {
        var change = new FieldChange
        {
            FieldName = "test",
            FieldType = fieldType
        };

        change.IsNumeric.ShouldBe(expected);
        await Task.CompletedTask;
    }

    [Test]
    public async Task FieldChange_IsNumeric_ShouldBeFalse_WhenFieldTypeIsNull()
    {
        var change = new FieldChange
        {
            FieldName = "test",
            FieldType = null
        };

        change.IsNumeric.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task FieldChange_DefaultValues_ShouldBeCorrect()
    {
        var change = new FieldChange { FieldName = "price" };

        change.Direction.ShouldBe(ChangeDirection.Unknown);
        change.Trend.ShouldBe(TrendDirection.Unknown);
        change.ConsecutiveDirectionCount.ShouldBe(0);
        change.IsNewMinimum.ShouldBeFalse();
        change.IsNewMaximum.ShouldBeFalse();
        change.TriggeredAlerts.ShouldBeEmpty();
        change.OldValue.ShouldBeNull();
        change.NewValue.ShouldBeNull();
        change.OldNumericValue.ShouldBeNull();
        change.NewNumericValue.ShouldBeNull();
        change.AbsoluteChange.ShouldBeNull();
        change.PercentageChange.ShouldBeNull();
        change.LlmImportance.ShouldBeNull();
        change.ImportanceReason.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractedObject_Fields_ShouldDefaultToEmpty()
    {
        var obj = new ExtractedObject();

        obj.Fields.ShouldBeEmpty();
        obj.IdentityKey.ShouldBeNull();
        obj.Index.ShouldBe(0);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ObjectModification_FieldChanges_ShouldDefaultToEmpty()
    {
        var mod = new ObjectModification
        {
            IdentityKey = "key1",
            PreviousObject = new ExtractedObject(),
            CurrentObject = new ExtractedObject()
        };

        mod.FieldChanges.ShouldBeEmpty();
        await Task.CompletedTask;
    }
}
