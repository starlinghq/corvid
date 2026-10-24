using System.Globalization;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Sessions;

public sealed partial class SessionViewModel
{
    private string _statusGenerator = "";
    private string _statusContext = "";
    private string _statusTrailing = "";
    private IReadOnlyList<StatusTag> _statusTags = [];

    /// <summary>The status bar's generator: "claude (escalated) · cuda:0 · q0 · 410 ms".</summary>
    public string StatusGenerator { get => _statusGenerator; private set => SetProperty(ref _statusGenerator, value); }

    /// <summary>"Kev-0.8B, calibrated".</summary>
    public string StatusDecider => _state.Decider ?? "";

    /// <summary>The context gauge: "38%".</summary>
    public string StatusContext { get => _statusContext; private set => SetProperty(ref _statusContext, value); }

    /// <summary>The session's approval mode; null until the engine reports it.</summary>
    public ApprovalMode? Mode => _state.Mode;

    /// <summary>"tainted" while outside content is in context, "2 approvals" while prompts wait, "read-only", loading.</summary>
    public IReadOnlyList<StatusTag> StatusTags { get => _statusTags; private set => SetProperty(ref _statusTags, value); }

    /// <summary>"here · 2 jobs".</summary>
    public string StatusScheduler => _state.Scheduler ?? "";

    /// <summary>The workspace's branch: "main".</summary>
    public string StatusBranch => _state.Branch ?? "";

    /// <summary>The workspace and branch, for the rail's brand line: "corvid · main".</summary>
    public string WorkspaceText => _state.Workspace is { } workspace
        ? _state.Branch is { } branch ? $"{workspace} · {branch}" : workspace
        : "";

    /// <summary>The right-hand item: the running turn's time ("turn 48 s"), or "idle".</summary>
    public string StatusTrailing { get => _statusTrailing; private set => SetProperty(ref _statusTrailing, value); }

    private void UpdateStatus()
    {
        StatusGenerator = _state.Generator is { } generator
            ? string.Join(" · ", new[]
            {
                generator.Escalated ? $"{generator.Model} (escalated)" : generator.Model,
                generator.Device,
                string.Create(CultureInfo.InvariantCulture, $"q{generator.Queue}"),
                generator.Latency is { } latency ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(latency.TotalMilliseconds)} ms") : null,
            }.Where(part => !string.IsNullOrEmpty(part)))
            : _state.Model ?? "";
        StatusContext = _state.ContextFraction is { } fraction
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(fraction * 100)}%")
            : "";
        StatusTrailing = _state.Running && _state.TurnStartedAt is { } started
            ? $"turn {SessionText.Duration(Now - started)}"
            : "idle";

        var tags = new List<StatusTag>();
        if (_state.ReadOnly)
            tags.Add(new StatusTag("read-only", Tone.Grey, ReadOnlyNote));
        if (_state.Tainted)
        {
            var source = _state.TaintSource is { } from ? $": {from}" : "";
            tags.Add(new StatusTag("tainted", Tone.Amber,
                $"Outside content in context{source}. Network reads now ask; egress and new recipients offer only \"once\"."));
        }

        if (!_state.Approvals.IsEmpty)
        {
            tags.Add(new StatusTag(SessionText.Plural(_state.Approvals.Count, "approval"), Tone.Amber,
                "Waiting on you: the card is in the transcript. Enter focuses it, a second Enter answers it."));
        }
        else if (_state.Question is { } question)
        {
            tags.Add(new StatusTag("a question", Tone.Blue, SafeText.Clean(question.Title ?? question.Text)));
        }

        if (_state.Loading is { } loading)
        {
            tags.Add(new StatusTag(string.Create(CultureInfo.InvariantCulture, $"loading {loading.Model} {loading.Index} of {loading.Count}"),
                Tone.Grey, "Models load after the window opens (K-4)."));
        }

        if (!tags.SequenceEqual(_statusTags))
            StatusTags = tags;

        OnPropertyChanged(nameof(StatusDecider));
        OnPropertyChanged(nameof(StatusScheduler));
        OnPropertyChanged(nameof(StatusBranch));
        OnPropertyChanged(nameof(WorkspaceText));
        OnPropertyChanged(nameof(Mode));
    }
}
