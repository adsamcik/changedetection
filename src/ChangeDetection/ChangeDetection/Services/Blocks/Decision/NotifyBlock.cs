using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Blocks.Decision;

/// <summary>
/// Sends a notification when the upstream condition signal is true.
/// Honors the configured channel when one is provided in the block configuration.
/// </summary>
public class NotifyBlock : IPipelineBlock
{
    public string BlockType => "Notify";

    public IReadOnlyList<PortDescriptor> InputPorts =>
    [
        new PortDescriptor { Name = "signal", Type = PortType.BooleanSignal },
        new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects, Required = false }
    ];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "notification", Type = PortType.Notification }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Delivery;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        if (context.IsDryRun)
            return BlockResult.Skip("Preview mode — notifications suppressed");

        if (context.IsFirstRun)
            return BlockResult.Skip("First run — baseline capture, no notifications");

        if (!TryGetSignal(context, out var signalValue))
            return BlockResult.Skip("No signal input");

        if (!signalValue)
            return BlockResult.Skip("No notification needed");

        var (channel, template) = ReadConfig(context);

        var summary = template ?? "Notification triggered";
        context.Logger.LogInformation(
            "NotifyBlock: channel={Channel}, summary={Summary}", channel ?? "default", summary);

        try
        {
            var notificationService = context.Services.GetService(typeof(INotificationService)) as INotificationService;
            if (notificationService is not null)
            {
                var watchRepo = context.Services.GetService(typeof(IRepository<WatchedSite>)) as IRepository<WatchedSite>;
                var watch = watchRepo is not null
                    ? await watchRepo.GetByIdAsync(context.WatchId, context.CancellationToken)
                    : null;

                if (watch is not null)
                {
                    var changeEvent = new ChangeEvent
                    {
                        WatchedSiteId = context.WatchId,
                        OwnerId = watch.OwnerId,
                        DetectedAt = DateTime.UtcNow,
                        ChangeType = ChangeType.Modified,
                        DiffSummary = summary,
                        BriefSummary = summary
                    };

                    await notificationService.SendNotificationAsync(
                        watch, changeEvent, summary, channel, context.CancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.LogWarning(ex, "NotifyBlock: Failed to send notification via service");
        }

        var output = JsonSerializer.SerializeToElement(new
        {
            sent = true,
            channel = channel ?? "default",
            summary
        });

        return BlockResult.Succeeded(output);
    }

    private static bool TryGetSignal(BlockContext context, out bool signal)
    {
        signal = false;

        if (!context.Inputs.TryGetValue("signal", out var signalElement))
            return false;

        if (signalElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            signal = signalElement.GetBoolean();
            return true;
        }

        if (signalElement.ValueKind == JsonValueKind.Object &&
            signalElement.TryGetProperty("signal", out var inner) &&
            inner.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            signal = inner.GetBoolean();
            return true;
        }

        return false;
    }

    private static (string? channel, string? template) ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return (null, null);

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return (null, null);

        string? channel = null, template = null;

        if (config.TryGetProperty("channel", out var chElem) && chElem.ValueKind == JsonValueKind.String)
            channel = chElem.GetString();

        if (config.TryGetProperty("template", out var tmplElem) && tmplElem.ValueKind == JsonValueKind.String)
            template = tmplElem.GetString();

        return (channel, template);
    }
}
