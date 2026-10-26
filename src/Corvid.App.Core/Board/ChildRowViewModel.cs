using System.Globalization;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Board;

/// <summary>
/// A5 (docs/plan/ui.md §4.13, F18): one child session as a row under its parent, on the parent's Board card and in the
/// session header's children: its goal, status, toolset, budget, model mix and its answer (the validated output when it
/// had a schema). Open shows the child read-only; Stop ends a running one. Text is cleaned (<see cref="SafeText"/>).
/// </summary>
public sealed class ChildRowViewModel : ObservableObject
{
    private ChildSessionChanged _child;
    private string _status = "";
    private Tone _tone;
    private string _details = "";
    private string _output = "";
    private string _automationName = "";

    public ChildRowViewModel(ChildSessionChanged child, Action<string> open, Func<string, Task>? stop = null)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(open);
        _child = child;
        OpenCommand = new RelayCommand(() => open(Id));
        StopCommand = new RelayCommand(() => _ = stop?.Invoke(Id), () => stop is not null && IsRunning);
        Refresh();
    }

    public string Id => _child.ChildId;

    public ChildSessionChanged Child => _child;

    /// <summary>"↳ summarise the three docs".</summary>
    public string Goal => "↳ " + Short(SafeText.Clean(_child.Goal), 120);

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public Tone StatusTone { get => _tone; private set => SetProperty(ref _tone, value); }

    /// <summary>"list_notes, read_file · 8 steps · 64,000 tokens · 600 s · generator qwen3.5-9b".</summary>
    public string Details { get => _details; private set => SetProperty(ref _details, value); }

    /// <summary>Its answer or why it ended, in a line or two; empty while it runs.</summary>
    public string Output { get => _output; private set => SetProperty(ref _output, value); }

    public bool HasOutput => Output.Length > 0;

    public bool IsRunning => _child.IsRunning;

    public string AutomationName { get => _automationName; private set => SetProperty(ref _automationName, value); }

    public RelayCommand OpenCommand { get; }

    public RelayCommand StopCommand { get; }

    public void Update(ChildSessionChanged child)
    {
        ArgumentNullException.ThrowIfNull(child);
        _child = child;
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Goal));
        Status = StatusLabel(_child);
        StatusTone = _child.Status switch
        {
            ChildSessionChanged.Running => Tone.Blue,
            "answered" => Tone.Green,
            "cancelled" or "unfinished" => Tone.Grey,
            "stuck" or "step_budget_exhausted" or "token_budget_exhausted" or "timed_out" => Tone.Amber,
            _ => Tone.Red,
        };
        var parts = new List<string> { _child.Tools.IsDefaultOrEmpty ? "no tools" : string.Join(", ", _child.Tools.Select(SafeText.Clean)) };
        if (_child.Budget is { } budget)
            parts.Add(budget.Text);
        parts.Add(_child.Mix is { Length: > 0 } mix ? SafeText.Clean(mix) : "the parent's models");
        if (_child.Tainted)
            parts.Add("tainted");
        Details = string.Join(" · ", parts);
        Output = _child.IsRunning ? ""
            : _child.Status == "answered" && _child.Answer is { Length: > 0 } answer
                ? (_child.HasSchema ? (_child.OutputValid == true ? "output (schema met): " : "output: ") : "") + Short(SafeText.Clean(answer), 240)
                : _child.Detail is { Length: > 0 } detail ? Short(SafeText.Clean(detail), 240) : "";
        OnPropertyChanged(nameof(HasOutput));
        OnPropertyChanged(nameof(IsRunning));
        StopCommand.NotifyCanExecuteChanged();
        AutomationName = $"Child session {Id}: {SafeText.Clean(_child.Goal)}. {Status}. {Details}. {Output}".Trim();
    }

    /// <summary>"running", "answered in 3 steps", "stopped: took its 8 steps".</summary>
    public static string StatusLabel(ChildSessionChanged child)
    {
        ArgumentNullException.ThrowIfNull(child);
        var name = child.Status.Replace('_', ' ');
        return child.Steps is { } steps && !child.IsRunning
            ? string.Create(CultureInfo.InvariantCulture, $"{name} · {steps} step{(steps == 1 ? "" : "s")}")
            : name;
    }

    private static string Short(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > max ? line[..max] + "…" : line;
    }
}
