using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ChangeDetection.Tests.Pipeline.Blocks;

/// <summary>
/// Fluent builder for creating <see cref="BlockContext"/> instances in tests.
/// </summary>
public class BlockContextBuilder
{
    private Guid _watchId = Guid.NewGuid();
    private DateTime _runTimestamp = DateTime.UtcNow;
    private string _blockInstanceId = $"block-{Guid.NewGuid():N}";
    private readonly Dictionary<string, JsonElement> _inputs = [];
    private CancellationToken _cancellationToken = CancellationToken.None;
    private ILogger _logger = NullLogger.Instance;
    private IBlockStateStore _stateStore;
    private object? _page;
    private IServiceProvider _services = Substitute.For<IServiceProvider>();
    private bool _isFirstRun;
    private bool _isDryRun;
    private PipelineDefinition? _pipelineDefinition;
    private JsonElement? _previousOutput;

    public BlockContextBuilder()
    {
        _stateStore = Substitute.For<IBlockStateStore>();
        _stateStore
            .GetPreviousOutputAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((JsonElement?)null);
    }

    public BlockContextBuilder WithWatchId(Guid watchId)
    {
        _watchId = watchId;
        return this;
    }

    public BlockContextBuilder WithBlockInstanceId(string blockInstanceId)
    {
        _blockInstanceId = blockInstanceId;
        return this;
    }

    public BlockContextBuilder WithInput(string portName, JsonElement value)
    {
        _inputs[portName] = value;
        return this;
    }

    public BlockContextBuilder WithInput(string portName, object value)
    {
        var json = JsonSerializer.Serialize(value);
        _inputs[portName] = JsonDocument.Parse(json).RootElement.Clone();
        return this;
    }

    public BlockContextBuilder WithPreviousOutput(JsonElement? output)
    {
        _previousOutput = output;
        _stateStore
            .GetPreviousOutputAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_previousOutput);
        return this;
    }

    public BlockContextBuilder WithPage(object page)
    {
        _page = page;
        return this;
    }

    public BlockContextBuilder WithServices(IServiceProvider services)
    {
        _services = services;
        return this;
    }

    public BlockContextBuilder WithFirstRun(bool isFirstRun = true)
    {
        _isFirstRun = isFirstRun;
        return this;
    }

    public BlockContextBuilder WithDryRun(bool isDryRun = true)
    {
        _isDryRun = isDryRun;
        return this;
    }

    public BlockContextBuilder WithLogger(ILogger logger)
    {
        _logger = logger;
        return this;
    }

    public BlockContextBuilder WithCancellationToken(CancellationToken ct)
    {
        _cancellationToken = ct;
        return this;
    }

    public BlockContextBuilder WithPipelineDefinition(PipelineDefinition? definition)
    {
        _pipelineDefinition = definition;
        return this;
    }

    public BlockContextBuilder WithStateStore(IBlockStateStore stateStore)
    {
        _stateStore = stateStore;
        return this;
    }

    public BlockContext Build() => new()
    {
        WatchId = _watchId,
        RunTimestamp = _runTimestamp,
        BlockInstanceId = _blockInstanceId,
        Inputs = new Dictionary<string, JsonElement>(_inputs),
        CancellationToken = _cancellationToken,
        Logger = _logger,
        StateStore = _stateStore,
        Page = _page,
        Services = _services,
        IsFirstRun = _isFirstRun,
        IsDryRun = _isDryRun,
        PipelineDefinition = _pipelineDefinition
    };

    /// <summary>
    /// Creates a <see cref="PipelineDefinition"/> containing a single block.
    /// </summary>
    public static PipelineDefinition CreateSingleBlockPipeline(string blockId, string blockType, object? config = null) => new()
    {
        SchemaVersion = 1,
        Blocks =
        [
            new BlockDefinition
            {
                Id = blockId,
                Type = blockType,
                Config = config switch
                {
                    JsonElement je => je,
                    not null => JsonSerializer.SerializeToElement(config),
                    _ => default
                }
            }
        ],
        Connections = []
    };
}
