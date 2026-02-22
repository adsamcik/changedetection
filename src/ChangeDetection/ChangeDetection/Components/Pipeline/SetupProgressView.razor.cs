using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Setup;
using Microsoft.AspNetCore.Components;

namespace ChangeDetection.Components.Pipeline;

public partial class SetupProgressView
{
    [Parameter] public IAsyncEnumerable<SetupProgress>? ProgressStream { get; set; }
    [Parameter] public EventCallback<bool> OnCheckpoint1Response { get; set; }
    [Parameter] public EventCallback<bool> OnCheckpoint2Response { get; set; }

    private readonly List<SetupProgress> _progressEntries = [];
    private int _currentCheckpoint;
    private ParsedIntent? _checkpointIntent;
    private PipelineProposal? _proposal;

    protected override async Task OnParametersSetAsync()
    {
        if (ProgressStream is null)
            return;

        _progressEntries.Clear();
        _currentCheckpoint = 0;
        _checkpointIntent = null;
        _proposal = null;

        await foreach (var progress in ProgressStream)
        {
            _progressEntries.Add(progress);

            if (progress.Type == SetupProgressType.CheckpointReached)
            {
                if (progress.Phase == SetupPhase.Checkpoint1)
                {
                    _currentCheckpoint = 1;
                    _checkpointIntent = progress.Intent;
                }
                else if (progress.Phase == SetupPhase.Checkpoint2)
                {
                    _currentCheckpoint = 2;
                    _proposal = progress.Proposal;
                }
            }

            StateHasChanged();
        }
    }

    private static string GetPhaseIcon(SetupPhase phase) => phase switch
    {
        SetupPhase.IntentParsing => "🧠",
        SetupPhase.ContentFetching => "🌐",
        SetupPhase.ContentAnalysis => "🔍",
        SetupPhase.Checkpoint1 => "✋",
        SetupPhase.PipelineBuilding => "🔧",
        SetupPhase.DryRun => "🧪",
        SetupPhase.AdversarialTest => "🛡️",
        SetupPhase.QcValidation => "✅",
        SetupPhase.Checkpoint2 => "✋",
        SetupPhase.Saving => "💾",
        _ => "⬜"
    };

    private static string GetTypeClass(SetupProgressType type) => type switch
    {
        SetupProgressType.Started => "fw-bold",
        SetupProgressType.Thinking => "text-muted fst-italic",
        SetupProgressType.Progress => "",
        SetupProgressType.CheckpointReached => "fw-bold text-primary",
        SetupProgressType.Completed => "fw-bold text-success",
        SetupProgressType.Failed => "fw-bold text-danger",
        _ => ""
    };
}
