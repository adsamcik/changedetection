using ChangeDetection.Core.Entities;
using Shouldly;

namespace ChangeDetection.Core.Tests.Entities;

[Category("Unit")]
public class ExtractionSchemaTests
{
    [Test]
    public async Task ItemSelector_ShouldBeSet()
    {
        var schema = new ExtractionSchema { ItemSelector = ".item" };

        schema.ItemSelector.ShouldBe(".item");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Version_ShouldDefaultToOne()
    {
        var schema = new ExtractionSchema { ItemSelector = ".item" };

        schema.Version.ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Fields_ShouldDefaultToEmpty()
    {
        var schema = new ExtractionSchema { ItemSelector = ".item" };

        schema.Fields.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IdentityFieldNames_ShouldDefaultToEmpty()
    {
        var schema = new ExtractionSchema { ItemSelector = ".item" };

        schema.IdentityFieldNames.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task DiffSettings_ShouldBeInitialized()
    {
        var schema = new ExtractionSchema { ItemSelector = ".item" };

        schema.DiffSettings.ShouldNotBeNull();
        schema.DiffSettings.Granularity.ShouldBe(DiffGranularity.Both);
        schema.DiffSettings.EnableImportanceScoring.ShouldBeTrue();
        schema.DiffSettings.DefaultImportance.ShouldBe(ChangeImportance.Medium);
        await Task.CompletedTask;
    }

    [Test]
    public async Task DiscoveredAt_ShouldBeRecentUtc()
    {
        var before = DateTime.UtcNow;
        var schema = new ExtractionSchema { ItemSelector = ".item" };
        var after = DateTime.UtcNow;

        schema.DiscoveredAt.ShouldBeGreaterThanOrEqualTo(before);
        schema.DiscoveredAt.ShouldBeLessThanOrEqualTo(after);
        await Task.CompletedTask;
    }

    [Test]
    public async Task SchemaField_DefaultValues_ShouldBeCorrect()
    {
        var field = new SchemaField { Name = "Price", Selector = ".price" };

        field.Type.ShouldBe(FieldType.String);
        field.IsRequired.ShouldBeFalse();
        field.IsIdentityField.ShouldBeFalse();
        field.SampleValue.ShouldBeNull();
        field.Confidence.ShouldBeNull();
        field.CurrencyCode.ShouldBeNull();
        field.DecimalPlaces.ShouldBeNull();
        field.FormatString.ShouldBeNull();
        field.TrackHistory.ShouldBeFalse();
        field.Unit.ShouldBeNull();
        field.AllowedValues.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task SchemaField_NumericTrackingDefaults_ShouldBeCorrect()
    {
        var field = new SchemaField { Name = "Stock", Selector = ".stock" };

        field.BaselineValue.ShouldBeNull();
        field.BaselineSetAt.ShouldBeNull();
        field.TrackingMode.ShouldBe(NumericTrackingMode.Both);
        field.MinSignificantChange.ShouldBeNull();
        field.MinSignificantChangePercent.ShouldBeNull();
        field.AlertThresholds.ShouldBeEmpty();
        field.CalculateTrend.ShouldBeTrue();
        field.TrendWindowSize.ShouldBe(10);
        field.TrackMinMax.ShouldBeTrue();
        field.HistoricalMin.ShouldBeNull();
        field.HistoricalMinAt.ShouldBeNull();
        field.HistoricalMax.ShouldBeNull();
        field.HistoricalMaxAt.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task FieldAlertThreshold_DefaultValues_ShouldBeCorrect()
    {
        var threshold = new FieldAlertThreshold();

        threshold.Id.ShouldNotBe(Guid.Empty);
        threshold.IsEnabled.ShouldBeTrue();
        threshold.OneTime.ShouldBeFalse();
        threshold.CooldownPeriod.ShouldBeNull();
        threshold.LastTriggeredAt.ShouldBeNull();
        threshold.TriggerCount.ShouldBe(0);
        threshold.NotificationTemplate.ShouldBeNull();
        threshold.ImportanceOverride.ShouldBeNull();
        threshold.Name.ShouldBeNull();
        await Task.CompletedTask;
    }
}
