using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.BlockExecution;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.BlockExecution;

[Category("Unit")]
public class PipelineSecurityValidatorTests : TestBase
{
    private readonly DomainPin _pin = DomainPin.FromUserUrl("https://example.com/start");

    [Test]
    public void Validate_WithValidPipeline_Passes()
    {
        var result = CreateSut().Validate(CreatePipeline("https://example.com/api"), _pin);

        result.IsValid.ShouldBeTrue();
        result.Violations.ShouldBeEmpty();
        result.PipelineFingerprint.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public void Validate_WithUnknownBlockType_AddsCriticalViolation()
    {
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                Block("input-1", "Input"),
                Block("evil-1", "RunShellCommand"),
                Block("output-1", "Output")
            ],
            Connections = []
        };

        var result = CreateSut().Validate(pipeline, _pin);

        result.IsValid.ShouldBeFalse();
        result.Violations.ShouldContain(v =>
            v.Rule == "BLOCK_ALLOWLIST" &&
            v.BlockId == "evil-1" &&
            v.Severity == SecuritySeverity.Critical);
    }

    [Test]
    public void Validate_WithUrlOutsidePinnedDomain_AddsCriticalViolation()
    {
        var result = CreateSut().Validate(CreatePipeline("https://evil.example.net/api"), _pin);

        result.IsValid.ShouldBeFalse();
        result.Violations.ShouldContain(v =>
            v.Rule == "DOMAIN_PIN" &&
            v.Severity == SecuritySeverity.Critical &&
            v.Detail.Contains("evil.example.net", StringComparison.Ordinal));
    }

    [Test]
    public void Validate_WithCycle_AddsCycleViolation()
    {
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                Block("input-1", "Input"),
                Block("transform-1", "Transform"),
                Block("output-1", "Output")
            ],
            Connections =
            [
                Connect("input-1", "data", "transform-1", "data"),
                Connect("transform-1", "data", "output-1", "data"),
                Connect("output-1", "data", "transform-1", "data")
            ]
        };

        var result = CreateSut().Validate(pipeline, _pin);

        result.IsValid.ShouldBeFalse();
        result.Violations.ShouldContain(v => v.Rule == "CYCLE");
    }

    [Test]
    public void Validate_WithTooManyBlocks_AddsStructuralViolation()
    {
        var blocks = new List<BlockDefinition> { Block("input-1", "Input") };
        blocks.AddRange(Enumerable.Range(0, 20).Select(index => Block($"transform-{index}", "Transform")));
        blocks.Add(Block("output-1", "Output"));

        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks = blocks,
            Connections = []
        };

        var result = CreateSut().Validate(pipeline, _pin);

        result.IsValid.ShouldBeFalse();
        result.Violations.ShouldContain(v =>
            v.Rule == "STRUCTURAL" &&
            v.Detail.Contains("exceeding the maximum", StringComparison.Ordinal));
    }

    private PipelineSecurityValidator CreateSut() =>
        new(new DomainPinValidator(CreateLogger<DomainPinValidator>()), CreateLogger<PipelineSecurityValidator>());

    private static PipelineDefinition CreatePipeline(string url) => new()
    {
        SchemaVersion = 1,
        Blocks =
        [
            Block("input-1", "Input"),
            Block("http-1", "HttpRequest", new { url }),
            Block("output-1", "Output")
        ],
        Connections =
        [
            Connect("input-1", "url", "http-1", "url"),
            Connect("http-1", "data", "output-1", "data")
        ]
    };

    private static BlockDefinition Block(string id, string type, object? config = null) => new()
    {
        Id = id,
        Type = type,
        Config = config is null ? null : JsonSerializer.SerializeToElement(config)
    };

    private static ConnectionDefinition Connect(string fromBlockId, string fromPort, string toBlockId, string toPort) =>
        new()
        {
            FromBlockId = fromBlockId,
            FromPort = fromPort,
            ToBlockId = toBlockId,
            ToPort = toPort
        };
}
