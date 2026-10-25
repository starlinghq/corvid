using System.Collections.ObjectModel;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Sessions;

/// <summary>A queued message's chip above the composer (<c>.queued</c>): "queued" or "steer", its text, and its ×.</summary>
public sealed class QueuedChip
{
    internal QueuedChip(QueuedMessage message, RelayCommand remove, RelayCommand takeBack, RelayCommand moveUp, RelayCommand moveDown)
    {
        Id = message.Id;
        Text = SafeText.Clean(message.Text);
        IsSteer = message.Steer;
        RemoveCommand = remove;
        TakeBackCommand = takeBack;
        MoveUpCommand = moveUp;
        MoveDownCommand = moveDown;
    }

    public string Id { get; }

    public string Text { get; }

    /// <summary>Delivered at the next step checkpoint rather than after the turn.</summary>
    public bool IsSteer { get; }

    /// <summary>The chip's muted lead: "queued" or "steer".</summary>
    public string Label => IsSteer ? "steer" : "queued";

    /// <summary>The × button: the message is dropped unsent.</summary>
    public RelayCommand RemoveCommand { get; }

    public string RemoveName => IsSteer ? "Remove the steer" : "Remove the queued message";

    /// <summary>Back into the editor, out of the queue (Up).</summary>
    public RelayCommand TakeBackCommand { get; }

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public string AutomationName => $"{Label}: {Text}";
}

/// <summary>A piece of a hint line: text, or a key drawn as a key cap (<c>kbd</c>).</summary>
public sealed record HintPart(string Text, bool IsKey);

public sealed partial class SessionViewModel
{
    /// <summary>The composer's placeholder, as the mockup prints it, with the escape that sends a line starting with # or ! as text (A4).</summary>
    public const string ComposerPlaceholder = "Ask Corvid…   / commands   @ files   ! shell   # memory   \\# as text";

    /// <summary>The placeholder where the service has no <c>!</c> or <c>#</c> mode (<see cref="ISessionService.HasComposerModes"/>).</summary>
    public const string ComposerPlaceholderWithoutModes = "Ask Corvid…   / commands   @ files";

    private static readonly IReadOnlyList<HintPart> RunningHint =
    [
        new("Ctrl", true), new(" ", false), new("↵", true), new(" steers at the next checkpoint · ", false), new("Esc", true),
        new(" interrupts", false),
    ];

    /// <summary>The running hint where the service cannot steer (<see cref="ISessionService.CanSteer"/>): Enter queues.</summary>
    private static readonly IReadOnlyList<HintPart> RunningHintWithoutSteering =
    [
        new("↵", true), new(" queues behind the turn · ", false), new("Esc", true), new(" interrupts", false),
    ];

    private static readonly IReadOnlyList<HintPart> ApprovalHint =
    [
        new("Your draft is kept. ", false), new("↵", true), new(" focuses the card, a second ", false), new("↵", true),
        new(" answers it.", false),
    ];

    private string _draft = "";
    private string? _stash;
    private string _stripText = "";
    private string _stripStatus = "";
    private IReadOnlyList<HintPart> _hint = [];

    /// <summary>The composer's text (two-way). Enter sends it, or queues it while a turn runs.</summary>
    public string Draft
    {
        get => _draft;
        set
        {
            if (!SetProperty(ref _draft, value ?? ""))
                return;
            OnPropertyChanged(nameof(DraftNote));
            OnPropertyChanged(nameof(TokenEstimate));
            RefreshComposerCommands();
            DraftChanged();
        }
    }

    public string Placeholder => _service.HasComposerModes ? ComposerPlaceholder : ComposerPlaceholderWithoutModes;

    /// <summary>
    /// The note beside the paperclip: "typed while the card opened: it never answers the card" while an approval card is
    /// open and the composer holds text; "draft saved" while a stashed draft waits (Ctrl+S).
    /// </summary>
    public string DraftNote => _currentApproval is not null && _draft.Length > 0
        ? "typed while the card opened: it never answers the card"
        : _stash is not null ? "draft saved" : "";

    /// <summary>"about 18 tokens": the draft's estimate, or the stashed draft's while the editor is empty.</summary>
    public string TokenEstimate => SessionText.TokenEstimate(_draft.Length > 0 ? _draft : _stash ?? "");

    /// <summary>The queue above the composer, in sending order.</summary>
    public ObservableCollection<QueuedChip> Queue { get; } = [];

    public bool HasQueue => Queue.Count > 0;

    // ---- The turn strip ----

    /// <summary>A turn is running (it may be waiting on the user).</summary>
    public bool IsRunning => _state.Running;

    /// <summary>The running turn waits on an approval or a question: the strip's amber clock instead of the spinner.</summary>
    public bool IsWaiting => _state.Running && _state.WaitingOnUser;

    /// <summary>The spinner shows: running and not waiting.</summary>
    public bool IsWorking => _state.Running && !_state.WaitingOnUser;

    /// <summary>The strip shows while a turn runs or messages are queued.</summary>
    public bool ShowTurnStrip => _state.Running || Queue.Count > 0;

    /// <summary>"step 4 of 6 · shell running · 12 s", "step 2 of 6 · waiting on you · 2 approvals".</summary>
    public string TurnStripText { get => _stripText; private set => SetProperty(ref _stripText, value); }

    /// <summary>
    /// The strip's words without the ticking time ("step 4 of 6 · shell running"), for a polite live region: it changes
    /// when the step or what runs changes, not every second (docs/plan/ui.md §4.15).
    /// </summary>
    public string TurnStripStatus { get => _stripStatus; private set => SetProperty(ref _stripStatus, value); }

    /// <summary>The strip's right-hand hint, with key caps: how to steer and interrupt, or how the card takes Enter.</summary>
    public IReadOnlyList<HintPart> Hint { get => _hint; private set => SetProperty(ref _hint, value); }

    public bool HasHint => _hint.Count > 0;

    /// <summary>
    /// Enter in the composer. While an approval card is open it focuses the card and sends nothing (the second Enter, on
    /// the card, answers it); with an open question the text answers it; a slash command runs; otherwise the text is sent,
    /// or queued while a turn runs.
    /// </summary>
    public RelayCommand SubmitCommand { get; private set; } = null!;

    /// <summary>
    /// Ctrl+Enter: delivered at the next step checkpoint while a turn runs (K-3); sent like Enter otherwise, and where the
    /// service cannot steer (<see cref="ISessionService.CanSteer"/>).
    /// </summary>
    public RelayCommand SteerCommand { get; private set; } = null!;

    /// <summary>Esc while a turn runs: interrupts it, keeping the work so far; the queue goes next.</summary>
    public RelayCommand InterruptCommand { get; private set; } = null!;

    /// <summary>Up on an empty composer: the last queued message back into the editor.</summary>
    public RelayCommand TakeBackLastCommand { get; private set; } = null!;

    /// <summary>Ctrl+S: stashes the draft, or brings the stashed one back into an empty editor.</summary>
    public RelayCommand StashDraftCommand { get; private set; } = null!;

    public bool CanSubmit => !IsReadOnly && (_draft.Trim().Length > 0 || _currentApproval is not null);

    private void InitializeComposer()
    {
        SubmitCommand = new RelayCommand(Submit, () => !IsReadOnly);
        SteerCommand = new RelayCommand(Steer, () => !IsReadOnly);
        InterruptCommand = new RelayCommand(() => Interrupt(), () => !IsReadOnly && _state.Running);
        TakeBackLastCommand = new RelayCommand(TakeBackLast, () => !IsReadOnly && Queue.Count > 0 && _draft.Length == 0);
        StashDraftCommand = new RelayCommand(Stash, () => _draft.Length > 0 || _stash is not null);
    }

    private void Submit()
    {
        if (IsReadOnly)
            return;
        if (_currentApproval is { } card)
        {
            // Text typed before the card appeared never answers it: this Enter only hands the keys to the card.
            card.IsFocused = true;
            OnPropertyChanged(nameof(DraftNote));
            FocusRequested?.Invoke(this, SessionFocusTarget.Approval);
            return;
        }

        var text = _draft.Trim();
        if (text.Length == 0)
            return;
        Draft = "";
        // A slash command runs as itself even with a question open: it is never the answer.
        if (IsCommandLine(text))
            Run(() => _service.RunCommandAsync(SessionId, text), () => GiveBack(text));
        else if (_pendingQuestion is { IsOpen: true, AllowsFreeText: true } question)
            // A refusal (answered from another view meanwhile, or a ! line the service will not take as an answer) gives the
            // text back to the composer.
            AnswerQuestion(question.QuestionId, text, () => GiveBack(text));
        else if (_state.Running)
            Run(() => _service.QueueAsync(SessionId, text), () => GiveBack(text));
        else
            Run(() => _service.SendAsync(SessionId, text), () => GiveBack(text));
    }

    private void Steer()
    {
        // Without steering here, Ctrl+Enter queues as Enter does rather than asking for what the engine refuses.
        if (!_state.Running || _currentApproval is not null || !_service.CanSteer)
        {
            Submit();
            return;
        }

        var text = _draft.Trim();
        if (text.Length == 0)
            return;
        Draft = "";
        Run(() => _service.SteerAsync(SessionId, text), () => GiveBack(text));
    }

    /// <summary>
    /// A refused message back in the composer (A4 verification): as it was when the composer is still empty; otherwise the
    /// person typed on meanwhile, and it goes before what they typed, on its own line, so neither is lost.
    /// </summary>
    private void GiveBack(string text) => Draft = _draft.Length == 0 ? text : $"{text}\n{_draft}";

    private void TakeBackLast()
    {
        if (Queue.Count > 0 && _draft.Length == 0)
            TakeBack(Queue[^1].Id);
    }

    private void TakeBack(string id) => Run(async () =>
    {
        var text = await _service.TakeBackQueuedAsync(SessionId, id).ConfigureAwait(true);
        if (text is not null)
            Draft = _draft.Length == 0 ? text : $"{_draft}\n{text}";
    });

    private void Stash()
    {
        if (_draft.Length > 0)
        {
            _stash = _draft;
            Draft = "";
        }
        else if (_stash is not null)
        {
            var stashed = _stash;
            _stash = null;
            Draft = stashed;
        }

        OnPropertyChanged(nameof(DraftNote));
        OnPropertyChanged(nameof(TokenEstimate));
        StashDraftCommand.NotifyCanExecuteChanged();
        DraftChanged();
    }

    /// <summary>Whether a draft waits put aside (Ctrl+S); there is room for one.</summary>
    public bool HasStashedDraft => _stash is not null;

    /// <summary>Puts a draft aside as Ctrl+S does (the demo opens with one, as Main.png shows "draft saved").</summary>
    public void StashDraft(string text)
    {
        _stash = string.IsNullOrEmpty(text) ? null : text;
        OnPropertyChanged(nameof(DraftNote));
        OnPropertyChanged(nameof(TokenEstimate));
        StashDraftCommand.NotifyCanExecuteChanged();
        DraftChanged();
    }

    /// <summary>
    /// A line is a command when its first word is <c>/</c> and a name, so a pasted path such as <c>/tmp/x</c> is a message
    /// (the registry's rule, <c>SlashCommandRegistry.IsCommand</c>).
    /// </summary>
    public static bool IsCommandLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var trimmed = line.TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != '/' || !char.IsAsciiLetter(trimmed[1]))
            return false;
        var end = trimmed.IndexOfAny([' ', '\t', '\n']);
        return (end < 0 ? trimmed[1..] : trimmed[1..end]).All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }

    private void UpdateComposer(SessionView previous)
    {
        if (!previous.Queue.Equals(_state.Queue))
        {
            Queue.Clear();
            var items = _state.Queue;
            for (var i = 0; i < items.Length; i++)
            {
                var message = items[i];
                var index = i;
                Queue.Add(new QueuedChip(message,
                    new RelayCommand(() => Run(() => _service.RemoveQueuedAsync(SessionId, message.Id))),
                    new RelayCommand(() => TakeBack(message.Id)),
                    new RelayCommand(() => Run(() => _service.MoveQueuedAsync(SessionId, message.Id, index - 1)), () => index > 0),
                    new RelayCommand(() => Run(() => _service.MoveQueuedAsync(SessionId, message.Id, index + 1)), () => index < items.Length - 1)));
            }

            OnPropertyChanged(nameof(HasQueue));
        }

        if (previous.Running != _state.Running || previous.WaitingOnUser != _state.WaitingOnUser)
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsWaiting));
            OnPropertyChanged(nameof(IsWorking));
            CompactCommand.NotifyCanExecuteChanged();
        }

        OnPropertyChanged(nameof(ShowTurnStrip));
        OnPropertyChanged(nameof(DraftNote));
        UpdateStrip();
        RefreshComposerCommands();
    }

    private void UpdateStrip()
    {
        TurnStripText = StripText(withTime: true);
        TurnStripStatus = StripText(withTime: false);
        var hint = _currentApproval is not null ? ApprovalHint
            : _state.Running ? _service.CanSteer ? RunningHint : RunningHintWithoutSteering
            : [];
        if (!ReferenceEquals(hint, _hint))
        {
            Hint = hint;
            OnPropertyChanged(nameof(HasHint));
        }
    }

    private string StripText(bool withTime)
    {
        if (!_state.Running)
            return "";
        var parts = new List<string>();
        if (_state.Step is { } step)
            parts.Add(_state.MaxSteps is { } max ? $"step {step} of {max}" : $"step {step}");
        if (_state.WaitingOnUser)
        {
            parts.Add("waiting on you");
            parts.Add(SessionText.Plural(_state.Approvals.Count, "approval"));
            return string.Join(" · ", parts);
        }

        var running = _state.Transcript.LastOrDefault(entry => entry is ToolEntry { Status: ToolCallStatus.InProgress } tool && tool.Turn == _state.Turn) as ToolEntry;
        if (running is not null)
        {
            parts.Add($"{SafeText.Clean(running.Tool)} running");
            if (withTime)
                parts.Add(SessionText.Duration(Now - running.StartedAt));
        }
        else
        {
            var thinking = _state.Transcript.Count > 0 && _state.Transcript[^1] is ThoughtEntry { Streaming: true };
            parts.Add(thinking ? "thinking" : "generating");
            if (withTime)
                parts.Add(SessionText.Duration(Now - (_state.ActivitySince ?? _state.TurnStartedAt ?? Now)));
        }

        return string.Join(" · ", parts);
    }

    private void RefreshComposerCommands()
    {
        OnPropertyChanged(nameof(CanSubmit));
        SubmitCommand.NotifyCanExecuteChanged();
        SteerCommand.NotifyCanExecuteChanged();
        InterruptCommand.NotifyCanExecuteChanged();
        TakeBackLastCommand.NotifyCanExecuteChanged();
        StashDraftCommand.NotifyCanExecuteChanged();
    }
}
