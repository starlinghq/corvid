using System.Globalization;
using Corvid.App.Core.Keys;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Palette;

/// <summary>
/// What the palette reaches beyond the shell: the open session, the prompt history, the session list, the model slots,
/// Settings' sections, and the Sessions page's find target. Each is optional so a test gives only what it exercises.
/// </summary>
public sealed record PaletteHost(ShellViewModel Shell, KeyMap Keys, TimeProvider Time, IDispatcher Dispatcher)
{
    /// <summary>The session the Sessions page shows (the rail's selection), or null.</summary>
    public Func<SessionViewModel?> CurrentSession { get; init; } = () => null;

    public IPromptHistory? History { get; init; }

    public ISessionService? Sessions { get; init; }

    public Func<IModelService>? Models { get; init; }

    /// <summary>Settings' sections by name, and the action that opens one.</summary>
    public IReadOnlyList<string> SettingsSections { get; init; } = [];

    public Action<string>? OpenSettings { get; init; }

    /// <summary>Brings a transcript block into view on the Sessions page (find in the session).</summary>
    public Action<string>? RevealBlock { get; init; }
}

/// <summary>
/// The command palette and the surfaces that share its shape (docs/plan/ui.md §3, §6): <c>Ctrl+K</c> and <c>Ctrl+P</c>
/// list every command, view and setting, fuzzy-matched; <c>Ctrl+Tab</c> switches sessions; <c>Ctrl+R</c> searches the
/// prompt history; <c>Ctrl+F</c> finds in the open session; <c>Ctrl+Shift+F</c> searches across sessions; <c>Alt+P</c>
/// switches the generator; <c>F1</c> and <c>?</c> list the keys. A key whose feature is not built yet (a new session, a
/// new window, an aside) opens a surface that says what it comes with, never nothing.
/// <para>
/// It is also where the window's own keys land: the shell maps a gesture to a command through the <see cref="KeyMap"/>
/// and calls <see cref="Execute"/>, so a remapped key and the palette's row do the same thing. The list is replaced whole
/// once per change of the query, capped at <see cref="MaxItems"/>; text from sessions, the log or the model is cleaned
/// of hidden characters, and a prompt that holds them cannot be put back into the composer.
/// </para>
/// </summary>
public sealed class PaletteViewModel : ObservableObject
{
    /// <summary>The most rows the list draws; the count line says how many more matched.</summary>
    public const int MaxItems = 50;

    private readonly PaletteHost _host;
    private CancellationTokenSource? _loading;
    private IReadOnlyList<PaletteItem> _source = [];
    private IReadOnlyList<PaletteItem> _items = [];
    private PaletteMode _mode;
    private bool _isOpen;
    private string _query = "";
    private int _selected = -1;
    private int _matched;
    private string _note = "";
    private Tone _noteTone = Tone.Blue;
    private string _title = "";
    private string _placeholder = "";
    private string _emptyText = "";
    private bool _isLoading;

    public PaletteViewModel(PaletteHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        CloseCommand = new RelayCommand(Close);
        AcceptCommand = new RelayCommand(Accept);
        ChooseCommand = new RelayCommand<PaletteItem>(Choose);
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public PaletteMode Mode
    {
        get => _mode;
        private set => SetProperty(ref _mode, value);
    }

    /// <summary>The surface's name, above the field ("Commands", "Switch session", "Find in …").</summary>
    public string Title { get => _title; private set => SetProperty(ref _title, value); }

    public string Placeholder { get => _placeholder; private set => SetProperty(ref _placeholder, value); }

    /// <summary>Whether the surface has a field to type in (a pending feature's surface has none).</summary>
    public bool HasQuery => _mode != PaletteMode.Pending;

    /// <summary>What is typed; every change refilters the list.</summary>
    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value ?? ""))
                Refilter();
        }
    }

    /// <summary>The rows shown, best match first; replaced whole on each change.</summary>
    public IReadOnlyList<PaletteItem> Items
    {
        get => _items;
        private set
        {
            if (!SetProperty(ref _items, value))
                return;
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(CountText));
        }
    }

    public bool HasItems => _items.Count > 0;

    public bool ShowEmpty => _items.Count == 0 && !_isLoading && _emptyText.Length > 0;

    public int SelectedIndex => _selected;

    public PaletteItem? Selected => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

    /// <summary>"12 matches", or "50 of 73 · type to narrow" when the list was capped.</summary>
    public string CountText => _mode == PaletteMode.Pending ? "" : _matched > _items.Count
        ? $"{_items.Count} of {_matched} · type to narrow"
        : _matched == 1 ? "1 match" : $"{_matched.ToString(CultureInfo.InvariantCulture)} matches";

    /// <summary>The line above the list: what a pending feature comes with, a failure, or what a choice did.</summary>
    public string Note { get => _note; private set { if (SetProperty(ref _note, value)) OnPropertyChanged(nameof(HasNote)); } }

    public bool HasNote => _note.Length > 0;

    public Tone NoteTone { get => _noteTone; private set => SetProperty(ref _noteTone, value); }

    /// <summary>What the empty list says ("No prompt matches.", "Type to find in Fix the sandbox test").</summary>
    public string EmptyText { get => _emptyText; private set { if (SetProperty(ref _emptyText, value)) OnPropertyChanged(nameof(ShowEmpty)); } }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
                OnPropertyChanged(nameof(ShowEmpty));
        }
    }

    public RelayCommand CloseCommand { get; }

    public RelayCommand AcceptCommand { get; }

    public RelayCommand<PaletteItem> ChooseCommand { get; }

    /// <summary>
    /// Runs one of the window's commands (<see cref="KeyCommands"/>), as its key or the palette's row does. A surface's key
    /// pressed while that surface is open moves through it instead (Ctrl+Tab again picks the next session).
    /// </summary>
    public void Execute(string command)
    {
        var shell = _host.Shell;
        switch (command)
        {
            case KeyCommands.Palette or KeyCommands.PaletteAlias:
                Open(PaletteMode.Commands);
                break;
            case KeyCommands.SessionSwitcher:
                if (_isOpen && _mode == PaletteMode.Sessions)
                    MoveSelection(1);
                else
                    Open(PaletteMode.Sessions);
                break;
            case KeyCommands.History:
                Open(PaletteMode.History);
                break;
            case KeyCommands.Find:
                Open(PaletteMode.Find);
                break;
            case KeyCommands.Search:
                Open(PaletteMode.Search);
                break;
            case KeyCommands.SwitchGenerator:
                Open(PaletteMode.Generator);
                break;
            case KeyCommands.Help or KeyCommands.HelpAlias:
                Open(PaletteMode.Help);
                break;
            case KeyCommands.NewSession:
                OpenPending("New session",
                    "New sessions come with the engine (A3), which opens one in the window's workspace; a session in another workspace needs G-4.",
                    [Go("Open the Board", "Every session at a glance", Area.Board), Go("Open Sessions", "The rail's sessions", Area.Sessions)]);
                break;
            case KeyCommands.NewWindow:
                OpenPending("New window",
                    "More windows come with pop-out windows: the runtime holds one workspace per window until G-4.",
                    [Go("Open the Board", "Several sessions side by side in this window", Area.Board)]);
                break;
            case KeyCommands.Aside:
                OpenAside();
                break;
            case KeyCommands.ToggleDock:
                Close();
                shell.ToggleDockCommand.Execute(null);
                break;
            case KeyCommands.ToggleRail:
                Close();
                shell.ToggleRailCommand.Execute(null);
                break;
            case KeyCommands.Changes:
                Close();
                shell.Navigate(Area.Review);
                break;
            case KeyCommands.Settings:
                Close();
                shell.Navigate(Area.Settings);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command, "Not one of the window's commands.");
        }
    }

    /// <summary>Opens a surface with an empty field and its first row selected.</summary>
    public void Open(PaletteMode mode)
    {
        Cancel();
        Mode = mode;
        OnPropertyChanged(nameof(HasQuery));
        _query = "";
        OnPropertyChanged(nameof(Query));
        Note = "";
        NoteTone = Tone.Blue;
        _source = [];
        IsOpen = true;
        switch (mode)
        {
            case PaletteMode.Commands:
                Present("Commands", "Type a command, a page or a setting…", "No command matches.", BuildCommands());
                break;
            case PaletteMode.Sessions:
                OpenSessions();
                break;
            case PaletteMode.History:
                Present("Prompt history", "Search prompts sent in this workspace…", "No prompt matches.", []);
                Load(LoadHistoryAsync);
                break;
            case PaletteMode.Find:
                OpenFind();
                break;
            case PaletteMode.Search:
                Present("Search sessions", "Search every session…", "No session matches.", []);
                Note = "This searches titles, previews and ids. Searching what was said comes with the episodic index (G-5).";
                Load(LoadSearchAsync);
                break;
            case PaletteMode.Help:
                Present("Keyboard shortcuts", "Filter the shortcuts…", "No shortcut matches.", BuildHelp());
                Note = "F1 or ? anywhere outside a text field. Settings › Keybindings remaps the window's own keys.";
                break;
            case PaletteMode.Generator:
                Present("Switch the generator", "Pick a model for this session's generator…", "No model can serve as the generator.", []);
                Load(LoadGeneratorAsync);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Pending surfaces open through Execute.");
        }
    }

    public void Close()
    {
        Cancel();
        if (!_isOpen)
            return;
        IsOpen = false;
        _source = [];
        Items = [];
        Select(-1);
    }

    /// <summary>Moves the selection through the enabled rows, wrapping at the ends.</summary>
    public void MoveSelection(int delta)
    {
        if (_items.Count == 0)
            return;
        var index = _selected;
        for (var step = 0; step < _items.Count; step++)
        {
            index = ((index + delta) % _items.Count + _items.Count) % _items.Count;
            if (_items[index].IsEnabled || _mode == PaletteMode.Help)
                break;
        }

        Select(index);
    }

    /// <summary>Enter: runs the selected row.</summary>
    public void Accept()
    {
        if (Selected is { } item)
            Choose(item);
    }

    /// <summary>A click on a row, or Enter on it: runs it, or says why it cannot run.</summary>
    public void Choose(PaletteItem? item)
    {
        if (item is null)
            return;
        Select(IndexOf(item));
        if (item.Run is not { } run)
        {
            if (item.Note.Length > 0)
                Show(item.Note, Tone.Amber);
            else if (_mode == PaletteMode.Help)
                Show($"{item.KeyText} belongs to {item.Group}: it works while the focus is there.", Tone.Blue);
            return;
        }

        try
        {
            run();
        }
        catch (Exception exception)
        {
            Show($"That did not work: {SafeText.Clean(exception.Message)}", Tone.Red);
        }
    }

    // ---- The surfaces ----

    private IReadOnlyList<PaletteItem> BuildCommands()
    {
        var shell = _host.Shell;
        var keys = _host.Keys;
        string Key(string id) => keys[id].KeyText;
        var items = new List<PaletteItem>();
        foreach (var area in Areas.Rail)
        {
            var key = area switch { Area.Settings => Key(KeyCommands.Settings), Area.Review => Key(KeyCommands.Changes), _ => "" };
            items.Add(new PaletteItem($"Go to {area.Title()}", "", Then(() => shell.Navigate(area)), "Pages", key, area.Icon()));
        }

        // The rail's footer holds Settings rather than its list.
        if (!Areas.Rail.Contains(Area.Settings))
            items.Add(new PaletteItem($"Go to {Area.Settings.Title()}", "", Then(() => shell.Navigate(Area.Settings)), "Pages", Key(KeyCommands.Settings), Area.Settings.Icon()));
        items.Add(new PaletteItem("Run the doctor", "The first run's checks: models, sandbox, Vault, GPU", Then(() => shell.Navigate(Area.FirstRun)), "Pages", "", IconKind.Check));
        items.Add(new PaletteItem("Open the attention tray", "Everything waiting on you, across sessions", Then(() => shell.OpenAttentionCommand.Execute(null)), "Pages", "", IconKind.Bell));

        items.Add(Command("Switch session…", "The sessions, most recent first", KeyCommands.SessionSwitcher, IconKind.Chat));
        items.Add(Command("Find in the session…", "Words in the open session's transcript", KeyCommands.Find, IconKind.Search));
        items.Add(Command("Search across sessions…", "Titles, previews and ids", KeyCommands.Search, IconKind.Search));
        items.Add(Command("Prompt history…", "Prompts sent in this workspace", KeyCommands.History, IconKind.Lines));
        items.Add(Command("Switch the generator…", "Without leaving the composer", KeyCommands.SwitchGenerator, IconKind.Chip));
        items.Add(Command("New session", "Comes with the engine (A3)", KeyCommands.NewSession, IconKind.Chat));
        items.Add(Command("New window", "Comes with pop-out windows", KeyCommands.NewWindow, IconKind.PopOut));
        items.Add(Command("Aside…", "A side question in a scratch fork; comes with F7", KeyCommands.Aside, IconKind.Question));
        items.Add(Command("Keyboard shortcuts", "Every key and where it works", KeyCommands.Help, IconKind.Question));

        items.Add(Command("Show or hide the dock", "", KeyCommands.ToggleDock, IconKind.PanelRight, "Window"));
        items.Add(Command("Show or hide the rail", "", KeyCommands.ToggleRail, IconKind.Menu, "Window"));
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark, AppTheme.System })
        {
            var name = theme.ToString().ToLowerInvariant();
            items.Add(new PaletteItem($"Theme: {name}", theme == shell.Theme ? "in use" : "", Then(() => shell.Theme = theme), "Window", "", IconKind.Sliders)
            {
                Keywords = "appearance colour color dark light",
            });
        }

        if (_host.CurrentSession() is { } session)
        {
            var title = SafeText.Clean(session.Title);
            items.Add(new PaletteItem("Rewind…", $"The checkpoint timeline of {title}", Then(() => shell.Navigate(Area.Rewind)), "Session", "Esc Esc", IconKind.Clock));
            items.Add(new PaletteItem("Expand the latest output", title, Then(() => { shell.Navigate(Area.Sessions); session.ExpandLatestOutputCommand.Execute(null); }), "Session", "Ctrl+O", IconKind.Terminal));
            items.Add(new PaletteItem("Show the thoughts", title, Then(() => { shell.Navigate(Area.Sessions); session.ToggleThoughtsCommand.Execute(null); }), "Session", "Ctrl+T", IconKind.Sparkle));
            foreach (var (slash, what) in new[] { ("/compact ", "Summarise older context, with a focus"), ("/model ", "Switch a slot's model"), ("/rollback ", "Roll back N turns"), ("/goal ", "Set the standing goal") })
            {
                items.Add(new PaletteItem($"{slash.Trim()}…", what, session.IsReadOnly ? null : Then(() => Compose(session, slash)), "Slash commands", "", IconKind.Terminal)
                {
                    Note = session.IsReadOnly ? "This session is read-only here." : "",
                    Keywords = slash,
                });
            }
        }

        foreach (var section in _host.SettingsSections)
        {
            items.Add(new PaletteItem($"Settings: {section}", "", _host.OpenSettings is { } open ? Then(() => open(section)) : null, "Settings", "", IconKind.Sliders)
            {
                Keywords = "preferences options configuration",
            });
        }

        return items;

        PaletteItem Command(string title, string detail, string id, IconKind icon, string group = "Commands") =>
            new(title, detail, () => Execute(id), group, Key(id), icon);
    }

    private void OpenSessions()
    {
        var shell = _host.Shell;
        var current = shell.CurrentSession?.Id;
        var items = shell.SessionGroups.SelectMany(group => group.Sessions).Select(session =>
        {
            var detail = $"{session.Id} · {session.StateLabel} · {session.When}";
            return new PaletteItem(SafeText.Clean(session.Title), session.Id == current ? $"{detail} · open" : detail,
                Then(() => shell.OpenSession(session.Id)), "", "", IconKind.Chat) { Keywords = session.Id };
        }).ToList();
        Present("Switch session", "Type a session's title or id…", "No session matches.", items);
        // Ctrl+Tab lands on the session before this one, as switching windows does.
        if (_items.Count > 1 && _items[0].Detail.EndsWith("· open", StringComparison.Ordinal))
            Select(1);
    }

    private void OpenFind()
    {
        if (_host.CurrentSession() is not { } session)
        {
            Present("Find in the session", "Find…", "", []);
            Show("Open a session first: Find looks through the open session's transcript.", Tone.Amber);
            return;
        }

        var title = SafeText.Clean(session.Title);
        Present($"Find in {title}", "Find in the transcript…", $"Type to find in {title}.", []);
    }

    private void OpenAside()
    {
        var session = _host.CurrentSession();
        var note = session is null
            ? "An aside asks about the open session, so it opens with one: open a session first."
            : $"An aside asks a side question in a scratch fork of {SafeText.Clean(session.Title)} at its head, and writes nothing back. It comes with the scratch fork (F7); during a running turn it waits on G-15.";
        IReadOnlyList<PaletteItem> items = session is null
            ? [Go("Open Sessions", "Pick a session in the rail", Area.Sessions)]
            : [new PaletteItem("Put the question in the composer", "Ask it in the session itself for now",
                session.IsReadOnly ? null : Then(() => Compose(session, "")), "", "", IconKind.Chat)
                { Note = session.IsReadOnly ? "This session is read-only here." : "" }];
        OpenPending("Aside", note, items);
    }

    private void OpenPending(string title, string note, IReadOnlyList<PaletteItem> alternatives)
    {
        Cancel();
        Mode = PaletteMode.Pending;
        OnPropertyChanged(nameof(HasQuery));
        _query = "";
        OnPropertyChanged(nameof(Query));
        IsOpen = true;
        Present(title, "", "", alternatives);
        Show(note, Tone.Amber);
    }

    private IReadOnlyList<PaletteItem> BuildHelp()
    {
        var keys = _host.Keys;
        return [.. KeyMap.Contexts.SelectMany(context => keys.Bindings.Where(binding => binding.Context == context.Context).Select(binding =>
            new PaletteItem(binding.Action, binding.ComesWith is { } with ? $"comes with {with}" : "",
                binding.Context == KeyMap.App && binding.Id is not (KeyCommands.Help or KeyCommands.HelpAlias) ? () => Execute(binding.Id) : null,
                context.Heading, binding.KeyText)
            {
                Keywords = binding.Id,
            }))];
    }

    private async Task LoadHistoryAsync(CancellationToken cancellationToken)
    {
        if (_host.History is not { } history)
        {
            Show("Prompt history comes with the engine's event log.", Tone.Amber);
            return;
        }

        var entries = await history.LoadAsync(cancellationToken);
        var zone = _host.Time.LocalTimeZone;
        var today = TimeZoneInfo.ConvertTime(_host.Time.GetUtcNow(), zone).Date;
        _source = [.. entries.Select(entry =>
        {
            var local = TimeZoneInfo.ConvertTime(entry.At, zone);
            var when = local.Date == today ? local.ToString("HH:mm", CultureInfo.InvariantCulture) : local.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
            var hidden = SafeText.HasHidden(entry.Text);
            return new PaletteItem(TranscriptText.Snippet(SafeText.Clean(entry.Text), "", 120), $"{SafeText.Clean(entry.SessionId)} · {when}",
                hidden ? null : Then(() => UsePrompt(entry.Text)), "", "", IconKind.Lines)
            {
                Note = hidden ? "This prompt holds hidden characters, so it is not put back; type it again." : "",
            };
        })];
        Refilter();
    }

    private async Task LoadSearchAsync(CancellationToken cancellationToken)
    {
        if (_host.Sessions is not { } sessions)
        {
            Show("Search comes with the engine's session list.", Tone.Amber);
            return;
        }

        var shell = _host.Shell;
        var summaries = await sessions.ListAsync(cancellationToken);
        _source = [.. summaries.Select(summary =>
        {
            var title = SafeText.Clean(summary.Title);
            var preview = SafeText.Clean(summary.Preview ?? "");
            var detail = string.Join(" · ", new[] { SafeText.Clean(summary.Id), SafeText.Clean(summary.Workspace), preview }.Where(part => part.Length > 0));
            var id = summary.Id;
            return new PaletteItem(title, detail, () =>
            {
                if (shell.OpenSession(id))
                    Close();
                else
                    Show($"{SafeText.Clean(id)} is not in the rail's list, so it cannot open here yet.", Tone.Amber);
            }, "", "", IconKind.Chat);
        })];
        Refilter();
    }

    private async Task LoadGeneratorAsync(CancellationToken cancellationToken)
    {
        if (_host.Models?.Invoke() is not { } models)
        {
            Show("Switching the generator comes with the engine's model slots.", Tone.Amber);
            return;
        }

        var snapshot = await models.LoadAsync(cancellationToken);
        var slot = snapshot.Slot(ModelSlotNames.Generator);
        _source = [.. snapshot.Catalogue.Where(model => model.CanServe.Contains(ModelSlotNames.Generator)).Select(model =>
        {
            var inUse = string.Equals(slot?.Model, model.Name, StringComparison.Ordinal);
            var where = SafeText.Clean(Spans.Plain(model.Where));
            var note = model.IsAvailable ? "" : model.Kind switch
            {
                ModelKind.Local => "Not downloaded: Models › Download… fetches it.",
                ModelKind.Cloud when model.Secret is { } secret => $"No key: set {SafeText.Clean(secret)} in Vault first (Secrets).",
                ModelKind.Server => "Not answering: Models checks the server.",
                _ => "Unavailable now: Models says why.",
            };
            var name = model.Name;
            return new PaletteItem(SafeText.Clean(name), inUse ? $"{where} · in use" : where,
                model.IsAvailable && !inUse ? () => Switch(models, name) : null, "", "", model.Kind == ModelKind.Cloud ? IconKind.Cloud : IconKind.Chip)
            {
                Note = inUse ? "" : note,
            };
        })];
        Refilter();
    }

    private void Switch(IModelService models, string model) => Load(async cancellationToken =>
    {
        var result = await models.SwitchAsync(ModelSlotNames.Generator, model, cancellationToken);
        if (!result.Written)
        {
            Show(SafeText.Clean(Spans.Plain(result.Message)), Tone.Red);
            return;
        }

        await LoadGeneratorAsync(cancellationToken);
        // Said here rather than in the service's words, which name the file it writes; Models shows those.
        Show($"The generator is now {SafeText.Clean(model)}: new sessions start on it, and this one switches after its issued tool calls.", Tone.Green);
    });

    // ---- Choosing ----

    private void UsePrompt(string text)
    {
        if (_host.CurrentSession() is not { } session)
        {
            Show("Open a session first: the prompt goes into its composer.", Tone.Amber);
            return;
        }

        if (session.IsReadOnly)
        {
            Show("This session is read-only here; open one you drive.", Tone.Amber);
            return;
        }

        Compose(session, text);
    }

    private void Compose(SessionViewModel session, string text)
    {
        Close();
        _host.Shell.Navigate(Area.Sessions);
        if (text.Length > 0)
            session.Draft = text;
        // After the page has drawn: the frame creates it in this call, but it focuses once loaded.
        _host.Dispatcher.Post(session.RequestComposerFocus);
    }

    private Action Then(Action action) => () =>
    {
        Close();
        action();
    };

    private PaletteItem Go(string title, string detail, Area area) =>
        new(title, detail, Then(() => _host.Shell.Navigate(area)), "", "", area.Icon());

    // ---- The list ----

    private void Present(string title, string placeholder, string empty, IReadOnlyList<PaletteItem> items)
    {
        Title = title;
        Placeholder = placeholder;
        EmptyText = empty;
        _source = items;
        Refilter();
    }

    private void Refilter()
    {
        IEnumerable<PaletteItem> matched;
        if (_mode == PaletteMode.Find)
        {
            matched = FindInSession();
        }
        else if (_query.Trim().Length == 0)
        {
            matched = _source;
        }
        else
        {
            var scored = _source
                .Select((item, index) => (item, index, score: FuzzyMatch.Best(_query, item.Title, item.Detail, item.Group, item.Keywords)))
                .Where(entry => entry.score is not null);
            // The shortcuts keep their grouping; everything else ranks best first.
            matched = (_mode == PaletteMode.Help ? scored : scored.OrderByDescending(entry => entry.score).ThenBy(entry => entry.index))
                .Select(entry => entry.item);
        }

        var all = matched.ToList();
        var grouped = _mode == PaletteMode.Help || _query.Trim().Length == 0;
        foreach (var item in all)
            item.Choose = ChooseCommand;
        for (var i = 0; i < all.Count; i++)
            all[i].GroupHeader = grouped && all[i].Group.Length > 0 && (i == 0 || all[i - 1].Group != all[i].Group) ? all[i].Group : "";
        _matched = all.Count;
        Items = all.Count > MaxItems ? all.GetRange(0, MaxItems) : all;
        OnPropertyChanged(nameof(CountText));
        Select(_items.Count == 0 ? -1 : FirstEnabled());
    }

    private IEnumerable<PaletteItem> FindInSession()
    {
        var query = _query.Trim();
        if (query.Length == 0 || _host.CurrentSession() is not { } session)
            return [];
        var shell = _host.Shell;
        var results = new List<PaletteItem>();
        foreach (var block in session.Blocks)
        {
            var text = TranscriptText.Of(block);
            if (text.Length == 0 || text.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            var id = block.Id;
            var where = block.Turn > 0 ? $"turn {block.Turn.ToString(CultureInfo.InvariantCulture)} · {TranscriptText.Who(block)}" : TranscriptText.Who(block);
            results.Add(new PaletteItem(TranscriptText.Snippet(SafeText.Clean(text), query), SafeText.Clean(where), Then(() =>
            {
                shell.Navigate(Area.Sessions);
                _host.RevealBlock?.Invoke(id);
            }), "", "", IconKind.Search));
        }

        return results;
    }

    private int FirstEnabled()
    {
        if (_mode == PaletteMode.Help)
            return 0;
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].IsEnabled)
                return i;
        }

        return 0;
    }

    private void Select(int index)
    {
        if (Selected is { } old)
            old.IsSelected = false;
        _selected = index;
        if (Selected is { } now)
            now.IsSelected = true;
        OnPropertyChanged(nameof(SelectedIndex));
        OnPropertyChanged(nameof(Selected));
    }

    private int IndexOf(PaletteItem item)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (ReferenceEquals(_items[i], item))
                return i;
        }

        return -1;
    }

    private void Show(string note, Tone tone)
    {
        NoteTone = tone;
        Note = note;
    }

    /// <summary>Runs a load for the open surface; a failure is shown in red, and a newer surface cancels it.</summary>
    private async void Load(Func<CancellationToken, Task> load)
    {
        Cancel();
        var cancellation = new CancellationTokenSource();
        _loading = cancellation;
        IsLoading = true;
        try
        {
            await load(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_loading, cancellation))
                Show($"Could not load: {SafeText.Clean(exception.Message)}", Tone.Red);
        }
        finally
        {
            if (ReferenceEquals(_loading, cancellation))
            {
                _loading = null;
                IsLoading = false;
            }

            cancellation.Dispose();
        }
    }

    private void Cancel()
    {
        _loading?.Cancel();
        _loading = null;
        IsLoading = false;
    }
}
