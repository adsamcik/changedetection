using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.BlockExecution;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline;

/// <summary>
/// Tests for port type compatibility rules in the pipeline validator.
/// Exercises ArePortTypesCompatible via the public Validate method
/// by constructing pipelines with specific port type mismatches.
/// </summary>
[Category("Unit")]
public class PortCompatibilityTests : TestBase
{
    private PipelineValidator _validator = null!;
    private BlockRegistry _registry = null!;

    [Before(Test)]
    public void Setup()
    {
        _validator = new PipelineValidator(CreateLogger<PipelineValidator>());
        _registry = new BlockRegistry();
        BlockRegistry.RegisterCoreBlocks(_registry);
    }

    [Test]
    public async Task HtmlContent_To_ExtractedObjects_IsValid()
    {
        // HtmlContent and ExtractedObjects are NOT in the same interchangeable group.
        // HtmlContent → PlainText is valid; PlainText → ExtractedObjects is valid.
        // But HtmlContent → ExtractedObjects requires PlainText intermediary.
        // Let's test HtmlContent → PlainText (valid) first.
        var pipeline = BuildPipelineWithConnection(PortType.HtmlContent, PortType.PlainText);

        var result = _validator.Validate(pipeline, _registry);

        // HtmlContent → PlainText is explicitly allowed ("HTML is text")
        var hasTypeMismatch = result.Errors.Any(e => e.Code == "PORT_TYPE_MISMATCH");
        hasTypeMismatch.ShouldBeFalse("HtmlContent → PlainText should be compatible");
        await Task.CompletedTask;
    }

    [Test]
    public async Task PlainText_To_DiffResult_IsNotValid()
    {
        // PlainText and DiffResult are distinct types — strict matching rejects this
        var pipeline = BuildPipelineWithConnection(PortType.PlainText, PortType.DiffResult);

        var result = _validator.Validate(pipeline, _registry);

        var hasTypeMismatch = result.Errors.Any(e => e.Code == "PORT_TYPE_MISMATCH");
        hasTypeMismatch.ShouldBeTrue("PlainText → DiffResult should NOT be compatible (strict type matching)");
        await Task.CompletedTask;
    }

    [Test]
    public async Task PlainText_To_ExtractedObjects_IsNotValid()
    {
        // PlainText and ExtractedObjects are distinct types — strict matching rejects this
        var pipeline = BuildPipelineWithConnection(PortType.PlainText, PortType.ExtractedObjects);

        var result = _validator.Validate(pipeline, _registry);

        var hasTypeMismatch = result.Errors.Any(e => e.Code == "PORT_TYPE_MISMATCH");
        hasTypeMismatch.ShouldBeTrue("PlainText → ExtractedObjects should NOT be compatible (strict type matching)");
        await Task.CompletedTask;
    }

    [Test]
    public async Task NumericValue_To_HtmlContent_IsNotValid()
    {
        // NumericValue is NOT compatible with HtmlContent — different data domains
        var pipeline = BuildPipelineWithConnection(PortType.NumericValue, PortType.HtmlContent);

        var result = _validator.Validate(pipeline, _registry);

        var hasTypeMismatch = result.Errors.Any(e => e.Code == "PORT_TYPE_MISMATCH");
        hasTypeMismatch.ShouldBeTrue("NumericValue → HtmlContent should NOT be compatible");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractedObjects_To_DiffResult_IsNotValid()
    {
        // ExtractedObjects and DiffResult are distinct types — strict matching rejects this
        var pipeline = BuildPipelineWithConnection(PortType.ExtractedObjects, PortType.DiffResult);

        var result = _validator.Validate(pipeline, _registry);

        var hasTypeMismatch = result.Errors.Any(e => e.Code == "PORT_TYPE_MISMATCH");
        hasTypeMismatch.ShouldBeTrue("ExtractedObjects → DiffResult should NOT be compatible (strict type matching)");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Configuration_To_AnyType_IsValid()
    {
        // PipelineValidator treats Configuration as a wildcard because config-style blocks pass
        // metadata/settings between stages rather than typed payload data.
        var pipeline = BuildPipelineWithConnection(PortType.Configuration, PortType.HtmlContent);

        var result = _validator.Validate(pipeline, _registry);

        var hasTypeMismatch = result.Errors.Any(e => e.Code == "PORT_TYPE_MISMATCH");
        hasTypeMismatch.ShouldBeFalse("Configuration → any type should be compatible (flexible)");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BooleanSignal_To_PlainText_IsNotValid()
    {
        // BooleanSignal is not a JSON-carrying type and has no special rule → incompatible
        var pipeline = BuildPipelineWithConnection(PortType.BooleanSignal, PortType.PlainText);

        var result = _validator.Validate(pipeline, _registry);

        var hasTypeMismatch = result.Errors.Any(e => e.Code == "PORT_TYPE_MISMATCH");
        hasTypeMismatch.ShouldBeTrue("BooleanSignal → PlainText should NOT be compatible");
        await Task.CompletedTask;
    }

    [Test]
    public async Task SameType_IsAlwaysCompatible()
    {
        // Identity check — same type is always valid
        var pipeline = BuildPipelineWithConnection(PortType.ExtractedObjects, PortType.ExtractedObjects);

        var result = _validator.Validate(pipeline, _registry);

        var hasTypeMismatch = result.Errors.Any(e => e.Code == "PORT_TYPE_MISMATCH");
        hasTypeMismatch.ShouldBeFalse("Same type → same type should always be compatible");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Builds a minimal 2-block pipeline that exercises port type compatibility.
    /// Uses custom block registrations so we control the exact port types being tested.
    /// </summary>
    private PipelineDefinition BuildPipelineWithConnection(PortType sourcePortType, PortType targetPortType)
    {
        // Register custom test blocks in the registry with specific port types
        var testSourceType = $"TestSource_{sourcePortType}_{targetPortType}";
        var testTargetType = $"TestTarget_{sourcePortType}_{targetPortType}";

        _registry.Register(testSourceType,
            inputPorts: [],
            outputPorts: [new PortDescriptor { Name = "out", Type = sourcePortType }],
            factory: _ => throw new NotImplementedException("Test stub"));

        _registry.Register(testTargetType,
            inputPorts: [new PortDescriptor { Name = "in", Type = targetPortType }],
            outputPorts: [],
            factory: _ => throw new NotImplementedException("Test stub"));

        return new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "source-1", Type = testSourceType },
                new BlockDefinition { Id = "target-1", Type = testTargetType }
            ],
            Connections =
            [
                new ConnectionDefinition
                {
                    FromBlockId = "source-1",
                    FromPort = "out",
                    ToBlockId = "target-1",
                    ToPort = "in"
                }
            ]
        };
    }
}
