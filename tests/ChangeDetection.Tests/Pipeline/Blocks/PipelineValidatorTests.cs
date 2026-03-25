using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Validation;
using ChangeDetection.Services.BlockExecution;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks;

[Category("Unit")]
public class PipelineValidatorTests : TestBase
{
    private PipelineValidator CreateValidator() =>
        new(CreateLogger<PipelineValidator>());

    private static BlockRegistry CreateRegistry()
    {
        var registry = new BlockRegistry();
        BlockRegistry.RegisterCoreBlocks(registry);
        return registry;
    }

    /// <summary>
    /// Builds a valid pipeline: Input→Navigate→Filter→ExtractSchema→HashCompare→Condition→Output
    /// </summary>
    private static PipelineDefinition BuildValidPipeline() => new()
    {
        SchemaVersion = 1,
        Blocks =
        [
            new BlockDefinition { Id = "input-1", Type = "Input" },
            new BlockDefinition { Id = "navigate-1", Type = "Navigate" },
            new BlockDefinition { Id = "filter-1", Type = "Filter" },
            new BlockDefinition { Id = "extract-1", Type = "ExtractSchema" },
            new BlockDefinition { Id = "hash-1", Type = "HashCompare" },
            new BlockDefinition { Id = "condition-1", Type = "Condition" },
            new BlockDefinition { Id = "output-1", Type = "Output" }
        ],
        Connections =
        [
            new ConnectionDefinition { FromBlockId = "input-1", FromPort = "url", ToBlockId = "navigate-1", ToPort = "url" },
            new ConnectionDefinition { FromBlockId = "navigate-1", FromPort = "html", ToBlockId = "filter-1", ToPort = "html" },
            new ConnectionDefinition { FromBlockId = "filter-1", FromPort = "html", ToBlockId = "extract-1", ToPort = "html" },
            new ConnectionDefinition { FromBlockId = "extract-1", FromPort = "data", ToBlockId = "hash-1", ToPort = "data" },
            new ConnectionDefinition { FromBlockId = "hash-1", FromPort = "result", ToBlockId = "condition-1", ToPort = "result" },
            new ConnectionDefinition { FromBlockId = "extract-1", FromPort = "data", ToBlockId = "output-1", ToPort = "data" }
        ]
    };

    [Test]
    public async Task Validate_ValidPipeline_ReturnsValid()
    {
        var validator = CreateValidator();
        var registry = CreateRegistry();
        var pipeline = BuildValidPipeline();

        var result = validator.Validate(pipeline, registry);

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_MissingInputBlock_ReturnsError()
    {
        var validator = CreateValidator();
        var registry = CreateRegistry();
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "navigate-1", Type = "Navigate" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "navigate-1", FromPort = "html", ToBlockId = "output-1", ToPort = "data" }
            ]
        };

        var result = validator.Validate(pipeline, registry);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "MISSING_INPUT_BLOCK");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_MissingOutputBlock_ReturnsError()
    {
        var validator = CreateValidator();
        var registry = CreateRegistry();
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "input-1", Type = "Input" },
                new BlockDefinition { Id = "navigate-1", Type = "Navigate" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "input-1", FromPort = "url", ToBlockId = "navigate-1", ToPort = "url" }
            ]
        };

        var result = validator.Validate(pipeline, registry);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "MISSING_OUTPUT_BLOCK");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_DuplicateBlockId_ReturnsError()
    {
        var validator = CreateValidator();
        var registry = CreateRegistry();
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "input-1", Type = "Input" },
                new BlockDefinition { Id = "input-1", Type = "Navigate" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "input-1", FromPort = "url", ToBlockId = "output-1", ToPort = "data" }
            ]
        };

        var result = validator.Validate(pipeline, registry);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "DUPLICATE_BLOCK_ID");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_DuplicateBlockId_IgnoresCase()
    {
        var validator = CreateValidator();
        var registry = CreateRegistry();
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "BlockA", Type = "Input" },
                new BlockDefinition { Id = "blocka", Type = "Navigate" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "BlockA", FromPort = "url", ToBlockId = "output-1", ToPort = "data" }
            ]
        };

        var result = validator.Validate(pipeline, registry);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "DUPLICATE_BLOCK_ID" && e.BlockId == "blocka");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_UnknownBlockType_ReturnsError()
    {
        var validator = CreateValidator();
        var registry = CreateRegistry();
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "input-1", Type = "Input" },
                new BlockDefinition { Id = "foobar-1", Type = "FooBar" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "input-1", FromPort = "url", ToBlockId = "output-1", ToPort = "data" }
            ]
        };

        var result = validator.Validate(pipeline, registry);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "UNKNOWN_BLOCK_TYPE" && e.BlockId == "foobar-1");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_InvalidConnectionSource_ReturnsError()
    {
        var validator = CreateValidator();
        var registry = CreateRegistry();
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "input-1", Type = "Input" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "nonexistent", FromPort = "url", ToBlockId = "output-1", ToPort = "data" }
            ]
        };

        var result = validator.Validate(pipeline, registry);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "INVALID_CONNECTION_SOURCE");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_InvalidConnectionTarget_ReturnsError()
    {
        var validator = CreateValidator();
        var registry = CreateRegistry();
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "input-1", Type = "Input" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "input-1", FromPort = "url", ToBlockId = "nonexistent", ToPort = "url" }
            ]
        };

        var result = validator.Validate(pipeline, registry);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "INVALID_CONNECTION_TARGET");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_InvalidPortName_ReturnsError()
    {
        var validator = CreateValidator();
        var registry = CreateRegistry();
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "input-1", Type = "Input" },
                new BlockDefinition { Id = "navigate-1", Type = "Navigate" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "input-1", FromPort = "nonexistent_port", ToBlockId = "navigate-1", ToPort = "url" },
                new ConnectionDefinition { FromBlockId = "navigate-1", FromPort = "html", ToBlockId = "output-1", ToPort = "data" }
            ]
        };

        var result = validator.Validate(pipeline, registry);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "INVALID_PORT_NAME");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_PortTypeMismatch_ReturnsError()
    {
        var validator = CreateValidator();
        var registry = CreateRegistry();
        // Connect Input's "url" (PortType.Url) to Filter's "html" (PortType.HtmlContent)
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "input-1", Type = "Input" },
                new BlockDefinition { Id = "filter-1", Type = "Filter" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "input-1", FromPort = "url", ToBlockId = "filter-1", ToPort = "html" },
                new ConnectionDefinition { FromBlockId = "filter-1", FromPort = "html", ToBlockId = "output-1", ToPort = "data" }
            ]
        };

        var result = validator.Validate(pipeline, registry);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "PORT_TYPE_MISMATCH");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_OrphanBlock_ReturnsError()
    {
        var validator = CreateValidator();
        var registry = CreateRegistry();
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "input-1", Type = "Input" },
                new BlockDefinition { Id = "navigate-1", Type = "Navigate" },
                new BlockDefinition { Id = "orphan-filter", Type = "Filter" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "input-1", FromPort = "url", ToBlockId = "navigate-1", ToPort = "url" },
                new ConnectionDefinition { FromBlockId = "navigate-1", FromPort = "html", ToBlockId = "output-1", ToPort = "data" }
            ]
        };

        var result = validator.Validate(pipeline, registry);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "ORPHAN_BLOCK" && e.BlockId == "orphan-filter");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_CycleDetected_ReturnsError()
    {
        var validator = CreateValidator();
        var registry = CreateRegistry();
        // Create a cycle: filter-a → extract-a → filter-a (via html→html would be type mismatch,
        // so use blocks that can form a type-valid cycle: Wait→Click→Wait would cycle)
        // For simplicity, just test cycle detection regardless of type errors
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "input-1", Type = "Input" },
                new BlockDefinition { Id = "navigate-1", Type = "Navigate" },
                new BlockDefinition { Id = "wait-1", Type = "Wait" },
                new BlockDefinition { Id = "click-1", Type = "Click" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "input-1", FromPort = "url", ToBlockId = "navigate-1", ToPort = "url" },
                new ConnectionDefinition { FromBlockId = "navigate-1", FromPort = "page", ToBlockId = "wait-1", ToPort = "page" },
                new ConnectionDefinition { FromBlockId = "wait-1", FromPort = "page", ToBlockId = "click-1", ToPort = "page" },
                new ConnectionDefinition { FromBlockId = "click-1", FromPort = "page", ToBlockId = "wait-1", ToPort = "page" },
                new ConnectionDefinition { FromBlockId = "navigate-1", FromPort = "html", ToBlockId = "output-1", ToPort = "data" }
            ]
        };

        var result = validator.Validate(pipeline, registry);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "CYCLE_DETECTED");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_RequiredInputNotConnected_ReturnsError()
    {
        var validator = CreateValidator();
        var registry = CreateRegistry();
        // Navigate block has required input "url" but no connection to it
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "input-1", Type = "Input" },
                new BlockDefinition { Id = "navigate-1", Type = "Navigate" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                // Input connects to output but not to navigate
                new ConnectionDefinition { FromBlockId = "input-1", FromPort = "config", ToBlockId = "output-1", ToPort = "data" }
            ]
        };

        var result = validator.Validate(pipeline, registry);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "REQUIRED_INPUT_NOT_CONNECTED" && e.BlockId == "navigate-1");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_SelfConnection_ReturnsError()
    {
        var validator = CreateValidator();
        var registry = CreateRegistry();
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "input-1", Type = "Input" },
                new BlockDefinition { Id = "navigate-1", Type = "Navigate" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "input-1", FromPort = "url", ToBlockId = "navigate-1", ToPort = "url" },
                new ConnectionDefinition { FromBlockId = "navigate-1", FromPort = "page", ToBlockId = "navigate-1", ToPort = "url" },
                new ConnectionDefinition { FromBlockId = "navigate-1", FromPort = "html", ToBlockId = "output-1", ToPort = "data" }
            ]
        };

        var result = validator.Validate(pipeline, registry);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "SELF_CONNECTION" && e.BlockId == "navigate-1");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_MultipleErrors_ReportsAll()
    {
        var validator = CreateValidator();
        var registry = CreateRegistry();
        // Pipeline with: no Input, no Output, unknown type, duplicate ID
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "nav-1", Type = "Navigate" },
                new BlockDefinition { Id = "nav-1", Type = "FooBar" }
            ],
            Connections = []
        };

        var result = validator.Validate(pipeline, registry);

        result.IsValid.ShouldBeFalse();
        result.Errors.Count.ShouldBeGreaterThanOrEqualTo(3);
        result.Errors.ShouldContain(e => e.Code == "MISSING_INPUT_BLOCK");
        result.Errors.ShouldContain(e => e.Code == "MISSING_OUTPUT_BLOCK");
        result.Errors.ShouldContain(e => e.Code == "DUPLICATE_BLOCK_ID");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validate_LlmBlocks_ReturnsWarning()
    {
        var validator = CreateValidator();
        var registry = CreateRegistry();
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "input-1", Type = "Input" },
                new BlockDefinition { Id = "navigate-1", Type = "Navigate" },
                new BlockDefinition { Id = "llm-1", Type = "LlmExtract" },
                new BlockDefinition { Id = "hash-1", Type = "HashCompare" },
                new BlockDefinition { Id = "condition-1", Type = "Condition" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "input-1", FromPort = "url", ToBlockId = "navigate-1", ToPort = "url" },
                new ConnectionDefinition { FromBlockId = "navigate-1", FromPort = "html", ToBlockId = "llm-1", ToPort = "html" },
                new ConnectionDefinition { FromBlockId = "llm-1", FromPort = "data", ToBlockId = "hash-1", ToPort = "data" },
                new ConnectionDefinition { FromBlockId = "hash-1", FromPort = "result", ToBlockId = "condition-1", ToPort = "result" },
                new ConnectionDefinition { FromBlockId = "llm-1", FromPort = "data", ToBlockId = "output-1", ToPort = "data" }
            ]
        };

        var result = validator.Validate(pipeline, registry);

        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldContain(w => w.Code == "LLM_COST_WARNING");
        await Task.CompletedTask;
    }
}
