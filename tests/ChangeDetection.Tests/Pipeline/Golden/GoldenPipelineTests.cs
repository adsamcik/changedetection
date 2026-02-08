using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Validation;
using ChangeDetection.Services.BlockExecution;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Golden;

[Category("Unit")]
public class GoldenPipelineTests : TestBase
{
    private BlockRegistry CreateRegistry()
    {
        var registry = new BlockRegistry();
        BlockRegistry.RegisterCoreBlocks(registry);
        return registry;
    }

    private IPipelineValidator CreateValidator() =>
        new PipelineValidator(CreateLogger<PipelineValidator>());

    [Test]
    [Arguments("amazon-price-tracking.json")]
    [Arguments("bbc-headline-changes.json")]
    [Arguments("simple-hash-check.json")]
    [Arguments("llm-content-evaluation.json")]
    [Arguments("multi-step-enrichment.json")]
    [Arguments("aggregated-job-postings.json")]
    public async Task GoldenPipeline_Deserializes_Successfully(string fixtureFile)
    {
        var json = await LoadFixtureAsync(fixtureFile);
        var pipeline = PipelineSerializer.Deserialize(json);

        pipeline.ShouldNotBeNull();
        pipeline.Blocks.Count.ShouldBeGreaterThan(0);
        pipeline.Connections.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    [Arguments("amazon-price-tracking.json")]
    [Arguments("bbc-headline-changes.json")]
    [Arguments("simple-hash-check.json")]
    [Arguments("llm-content-evaluation.json")]
    [Arguments("multi-step-enrichment.json")]
    [Arguments("aggregated-job-postings.json")]
    public async Task GoldenPipeline_PassesValidation(string fixtureFile)
    {
        var json = await LoadFixtureAsync(fixtureFile);
        var pipeline = PipelineSerializer.Deserialize(json);
        pipeline.ShouldNotBeNull();

        var registry = CreateRegistry();
        var validator = CreateValidator();
        var result = validator.Validate(pipeline, registry);

        result.IsValid.ShouldBeTrue(
            $"Validation failed for {fixtureFile}: {string.Join(", ", result.Errors.Select(e => $"[{e.Code}] {e.Message}"))}");
    }

    [Test]
    [Arguments("amazon-price-tracking.json")]
    [Arguments("bbc-headline-changes.json")]
    [Arguments("simple-hash-check.json")]
    [Arguments("llm-content-evaluation.json")]
    [Arguments("multi-step-enrichment.json")]
    [Arguments("aggregated-job-postings.json")]
    public async Task GoldenPipeline_HasInputAndOutput(string fixtureFile)
    {
        var json = await LoadFixtureAsync(fixtureFile);
        var pipeline = PipelineSerializer.Deserialize(json);
        pipeline.ShouldNotBeNull();

        pipeline.Blocks.ShouldContain(b => b.Type == "Input");
        pipeline.Blocks.ShouldContain(b => b.Type == "Output");
    }

    [Test]
    [Arguments("amazon-price-tracking.json")]
    public async Task GoldenPipeline_AmazonPrice_HasExpectedBlocks(string fixtureFile)
    {
        var json = await LoadFixtureAsync(fixtureFile);
        var pipeline = PipelineSerializer.Deserialize(json);
        pipeline.ShouldNotBeNull();

        pipeline.Blocks.ShouldContain(b => b.Type == "NumericDelta");
        pipeline.Blocks.ShouldContain(b => b.Type == "Condition");
        pipeline.Blocks.ShouldContain(b => b.Type == "Notify");
    }

    [Test]
    [Arguments("llm-content-evaluation.json")]
    public async Task GoldenPipeline_LlmEvaluation_HasLlmBlocks(string fixtureFile)
    {
        var json = await LoadFixtureAsync(fixtureFile);
        var pipeline = PipelineSerializer.Deserialize(json);
        pipeline.ShouldNotBeNull();

        pipeline.Blocks.ShouldContain(b => b.Type == "LlmExtract");
        pipeline.Blocks.ShouldContain(b => b.Type == "LlmEvaluate");
    }

    [Test]
    [Arguments("amazon-price-tracking.json")]
    [Arguments("bbc-headline-changes.json")]
    [Arguments("simple-hash-check.json")]
    public async Task GoldenPipeline_RoundTrips_Through_Serializer(string fixtureFile)
    {
        var json = await LoadFixtureAsync(fixtureFile);
        var pipeline = PipelineSerializer.Deserialize(json);
        pipeline.ShouldNotBeNull();

        var reserialized = PipelineSerializer.Serialize(pipeline);
        var reparsed = PipelineSerializer.Deserialize(reserialized);
        reparsed.ShouldNotBeNull();

        reparsed.Blocks.Count.ShouldBe(pipeline.Blocks.Count);
        reparsed.Connections.Count.ShouldBe(pipeline.Connections.Count);
    }

    private static async Task<string> LoadFixtureAsync(string filename)
    {
        var dir = Path.GetDirectoryName(typeof(GoldenPipelineTests).Assembly.Location)!;
        var path = Path.Combine(dir, "Pipeline", "Golden", filename);
        return await File.ReadAllTextAsync(path);
    }
}
