using System.Text.Json;
using System.Text.Json.Nodes;

namespace Corvid.App.Core.Keys;

/// <summary>The window's own commands, which the shell binds and Settings can remap (docs/plan/ui.md §6).</summary>
public static class KeyCommands
{
    public const string Palette = "palette";
    public const string PaletteAlias = "palette.alias";
    public const string NewSession = "session.new";
    public const string NewWindow = "window.new";
    public const string SessionSwitcher = "session.switcher";
    public const string Aside = "aside";
    public const string Find = "find";
    public const string Search = "search";
    public const string History = "history";
    public const string ToggleDock = "dock.toggle";
    public const string ToggleRail = "rail.toggle";
    public const string Changes = "changes";
    public const string Settings = "settings";
    public const string SwitchGenerator = "generator.switch";
    public const string Help = "help";
    public const string HelpAlias = "help.alias";
}

/// <summary>
/// One line of the keyboard map: what a key does and where. The shell's own bindings (context <c>app</c>) carry a
/// <see cref="Gesture"/> a person can change in Settings; the rest belong to a page (the composer, an approval card, a
/// list) and are listed as the page binds them, with <see cref="Gestures"/> for conflict checks where they are single keys.
/// </summary>
public sealed record KeyBinding(string Id, string Context, string Action, string DefaultText)
{
    /// <summary>The gesture in force for a remappable binding; null for a page's fixed keys.</summary>
    public KeyGesture? Gesture { get; init; }

    /// <summary>The gestures a fixed binding uses, for the conflict check ("Ctrl+1" to "Ctrl+7"); empty for sequences like Esc Esc.</summary>
    public IReadOnlyList<KeyGesture> Gestures { get; init; } = [];

    /// <summary>What the key needs that is not built yet ("the scratch fork, F7"); it still opens its surface, saying so.</summary>
    public string? ComesWith { get; init; }

    public bool IsRemappable => Gesture is not null;

    /// <summary>The key as drawn: the gesture in force, or the page's own text ("Ctrl+1 to Ctrl+7", "Esc Esc").</summary>
    public string KeyText => Gesture?.ToString() ?? DefaultText;

    /// <summary>Every single gesture the binding answers to.</summary>
    public IEnumerable<KeyGesture> AllGestures => Gesture is { } gesture ? [gesture] : Gestures;
}

/// <summary>What a change to a binding found: refused with a reason, or accepted with what it now shares a key with.</summary>
public sealed record KeyRebind(bool Accepted, string Problem, IReadOnlyList<KeyBinding> Conflicts, IReadOnlyList<KeyBinding> Shadows);

/// <summary>
/// The keyboard map (docs/plan/ui.md §6, §4.15): every default with its context, the remappings a person made, and the
/// checks Settings shows. Two bindings in one context on one gesture conflict (the second never fires); an <c>app</c>
/// binding on a gesture a page also uses is shadowed there (the page's key wins while it has focus), which is a note,
/// not an error. The shell rebinds whenever <see cref="Changed"/> fires; <see cref="ToJson"/> writes the remappings in
/// <c>keybindings.json</c>'s shape (docs/plan/tui.md §4.7), contexts holding gesture to command, <c>null</c> to unbind.
/// </summary>
public sealed class KeyMap
{
    public const string App = "app";

    // Written for a person to read: "Ctrl+K" keeps its "+" rather than a \u escape.
    private static readonly JsonSerializerOptions Written = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly List<KeyBinding> _bindings;
    private readonly Dictionary<string, KeyBinding> _defaults;

    public KeyMap()
    {
        _bindings = [.. Defaults()];
        _defaults = _bindings.ToDictionary(binding => binding.Id, StringComparer.Ordinal);
    }

    /// <summary>A remapping or a reset changed what a key does.</summary>
    public event EventHandler? Changed;

    /// <summary>Every binding in the map's order: the window's own first, then each page's.</summary>
    public IReadOnlyList<KeyBinding> Bindings => _bindings;

    public KeyBinding this[string id] => _bindings.First(binding => string.Equals(binding.Id, id, StringComparison.Ordinal));

    /// <summary>A binding as it ships, before any remapping.</summary>
    public KeyBinding DefaultOf(string id) => _defaults[id];

    /// <summary>The contexts as the help sheet groups them, with a heading each.</summary>
    public static IReadOnlyList<(string Context, string Heading)> Contexts { get; } =
    [
        (App, "Anywhere"), ("composer", "Composer"), ("transcript", "Transcript"), ("approval", "Approval card"), ("dock", "Dock"),
        ("review", "Review"), ("models", "Models"), ("audit", "Audit"), ("memory", "Memory"), ("board", "Board"), ("rewind", "Rewind"),
        ("skills", "Skills"), ("jobs", "Jobs"), ("mcp", "MCP"), ("gateway", "Gateway"), ("safety", "Safety"), ("secrets", "Secrets"),
    ];

    public static string Heading(string context) => Contexts.FirstOrDefault(pair => pair.Context == context).Heading ?? context;

    /// <summary>Whether any binding differs from its default.</summary>
    public bool IsChanged => _bindings.Any(binding => binding.Gesture != _defaults[binding.Id].Gesture);

    /// <summary>The app binding a gesture fires, if any; what the shell looks up when a key arrives.</summary>
    public KeyBinding? Find(KeyGesture gesture) =>
        _bindings.FirstOrDefault(binding => binding.Context == App && binding.Gesture == gesture);

    /// <summary>Bindings in the same context on the same gesture as this one.</summary>
    public IReadOnlyList<KeyBinding> ConflictsOf(KeyBinding binding) => Others(binding, sameContext: true);

    /// <summary>For an app binding: page bindings on the same gesture, which win while their page has focus.</summary>
    public IReadOnlyList<KeyBinding> ShadowsOf(KeyBinding binding) => binding.Context == App ? Others(binding, sameContext: false) : [];

    /// <summary>The contexts whose keys a page binds while its area shows.</summary>
    public static IReadOnlyList<string> ContextsOf(Area area) => area switch
    {
        Area.Sessions => ["composer", "transcript", "approval", "dock"],
        Area.Review => ["review"],
        Area.Models => ["models"],
        Area.Audit => ["audit"],
        Area.Memory => ["memory"],
        Area.Board => ["board"],
        Area.Rewind => ["rewind"],
        Area.Skills => ["skills"],
        Area.Jobs => ["jobs"],
        Area.Mcp => ["mcp"],
        Area.Gateway => ["gateway"],
        Area.Safety => ["safety"],
        Area.Secrets => ["secrets"],
        _ => [],
    };

    /// <summary>
    /// Whether a window key's gesture is also a key of the page that shows: there the page's key wins, so the window
    /// leaves the gesture to it (Ctrl+F filters Audit's log, Alt+P opens Models' Switch… picker).
    /// </summary>
    public bool IsPagesKey(KeyBinding binding, Area area)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var contexts = ContextsOf(area);
        return contexts.Count > 0 && ShadowsOf(binding).Any(shadow => contexts.Contains(shadow.Context));
    }

    /// <summary>
    /// Proposes a gesture for a remappable binding. A gesture that does not parse, or one another app binding uses, is
    /// refused and nothing changes; one a page uses is accepted with the pages named, since there the page's key wins.
    /// </summary>
    public KeyRebind Rebind(string id, string text)
    {
        var current = this[id];
        if (!current.IsRemappable)
            return new KeyRebind(false, $"{current.Action} is the page's own key; it is listed here but not remapped.", [], []);
        if (!KeyGesture.TryParse(text, out var gesture, out var problem))
            return new KeyRebind(false, problem, [], []);
        var proposed = current with { Gesture = gesture };
        var conflicts = Others(proposed, sameContext: true);
        if (conflicts.Count > 0)
            return new KeyRebind(false, $"{gesture} already {Lower(conflicts[0].Action)}; change that one first.", conflicts, []);
        Replace(proposed);
        return new KeyRebind(true, "", [], Others(proposed, sameContext: false));
    }

    /// <summary>Puts one binding back to its default.</summary>
    public void Reset(string id)
    {
        var standard = _defaults[id];
        if (this[id].Gesture == standard.Gesture)
            return;
        // A default can be taken meanwhile by another binding's remapping: that one goes back too.
        foreach (var taker in _bindings.Where(binding => binding.Id != id && binding.Context == standard.Context && binding.Gesture == standard.Gesture).ToList())
            Replace(_defaults[taker.Id]);
        Replace(standard);
    }

    public void ResetAll()
    {
        if (!IsChanged)
            return;
        for (var i = 0; i < _bindings.Count; i++)
            _bindings[i] = _defaults[_bindings[i].Id];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The remappings in <c>keybindings.json</c>'s shape: the app context, each changed gesture to its command, and each
    /// default given up mapped to null so it is unbound rather than left to the default.
    /// </summary>
    public string ToJson()
    {
        var app = new JsonObject();
        foreach (var binding in _bindings.Where(binding => binding.IsRemappable && binding.Gesture != _defaults[binding.Id].Gesture))
        {
            var standard = _defaults[binding.Id].Gesture!.ToString();
            if (!_bindings.Any(other => other.Gesture?.ToString() == standard))
                app[standard] = null;
            app[binding.Gesture!.ToString()] = binding.Id;
        }

        var root = new JsonObject { [App] = app };
        return root.ToJsonString(Written);
    }

    private IReadOnlyList<KeyBinding> Others(KeyBinding binding, bool sameContext)
    {
        var gestures = binding.AllGestures.ToHashSet();
        if (gestures.Count == 0)
            return [];
        return [.. _bindings.Where(other => other.Id != binding.Id
            && (other.Context == binding.Context) == sameContext
            && other.AllGestures.Any(gestures.Contains))];
    }

    private void Replace(KeyBinding binding)
    {
        var index = _bindings.FindIndex(item => item.Id == binding.Id);
        if (_bindings[index] == binding)
            return;
        _bindings[index] = binding;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string Lower(string action) => action.Length == 0 ? action : char.ToLowerInvariant(action[0]) + action[1..];

    private static IEnumerable<KeyBinding> Defaults()
    {
        KeyBinding App(string id, string action, string key, string? comesWith = null) =>
            new(id, KeyMap.App, action, key) { Gesture = KeyGesture.Parse(key), ComesWith = comesWith };
        KeyBinding Page(string context, string id, string action, string text, params string[] gestures) =>
            new(id, context, action, text) { Gestures = [.. gestures.Select(KeyGesture.Parse)] };
        KeyBinding PageKeys(string context, string id, string action, string text) => new(id, context, action, text);

        yield return App(KeyCommands.Palette, "Opens the command palette", "Ctrl+K");
        yield return App(KeyCommands.PaletteAlias, "Opens the command palette (the terminal's key)", "Ctrl+P");
        yield return App(KeyCommands.NewSession, "Starts a new session", "Ctrl+N", "the engine (A3)");
        yield return App(KeyCommands.NewWindow, "Opens a new window", "Ctrl+Shift+N", "pop-out windows");
        yield return App(KeyCommands.SessionSwitcher, "Switches between sessions", "Ctrl+Tab");
        yield return App(KeyCommands.Aside, "Asks an aside in a scratch fork", "Ctrl+;", "the scratch fork (F7)");
        yield return App(KeyCommands.Find, "Finds in the session", "Ctrl+F");
        yield return App(KeyCommands.Search, "Searches across sessions", "Ctrl+Shift+F");
        yield return App(KeyCommands.History, "Searches the prompt history", "Ctrl+R");
        yield return App(KeyCommands.ToggleDock, "Shows or hides the dock", "Ctrl+B");
        yield return App(KeyCommands.ToggleRail, "Shows or hides the rail", "Ctrl+Shift+B");
        yield return App(KeyCommands.Changes, "Opens the changes in Review", "Ctrl+Shift+C");
        yield return App(KeyCommands.Settings, "Opens Settings", "Ctrl+,");
        yield return App(KeyCommands.SwitchGenerator, "Switches the generator without leaving the composer", "Alt+P");
        yield return App(KeyCommands.Help, "Shows help and the shortcuts", "F1");
        yield return App(KeyCommands.HelpAlias, "Shows the shortcuts (outside a text field)", "?");

        yield return PageKeys("composer", "composer.send", "Sends; queues while a turn runs", "Enter");
        yield return Page("composer", "composer.newline", "Breaks the line", "Shift+Enter");
        yield return Page("composer", "composer.steer", "Steers at the next checkpoint", "Ctrl+Enter", "Ctrl+Enter");
        yield return PageKeys("composer", "composer.interrupt", "Interrupts the turn; the queue goes next", "Esc");
        yield return PageKeys("composer", "composer.rewind", "On an empty composer, opens the rewind timeline", "Esc Esc");
        yield return PageKeys("composer", "composer.takeback", "On an empty composer, takes the last queued message back", "Up");
        yield return Page("composer", "composer.mode", "Cycles the approval mode", "Shift+Tab");
        yield return Page("composer", "composer.stash", "Puts the draft aside", "Ctrl+S", "Ctrl+S");
        yield return Page("transcript", "transcript.output", "Expands the latest tool output", "Ctrl+O", "Ctrl+O");
        yield return Page("transcript", "transcript.thoughts", "Shows the thoughts", "Ctrl+T", "Ctrl+T");
        yield return Page("transcript", "transcript.decisions", "Shows every checkpoint's chips", "Ctrl+Shift+D", "Ctrl+Shift+D");
        yield return Page("dock", "dock.tabs", "Picks a dock tab", "Ctrl+1 to Ctrl+7",
            "Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+5", "Ctrl+6", "Ctrl+7");
        yield return PageKeys("approval", "approval.focus", "Focuses the open card (the first Enter)", "Enter");
        yield return PageKeys("approval", "approval.answer", "Answers the focused card", "1 to 5");
        yield return PageKeys("approval", "approval.move", "Moves between its answers", "Left, Right");
        yield return PageKeys("review", "review.files", "Moves through the changed files", "Up, Down");
        yield return Page("models", "models.switch", "Opens the generator's Switch… picker", "Alt+P", "Alt+P");
        yield return Page("models", "models.ladder", "Moves a focused escalation step", "Alt+Left, Alt+Right", "Alt+Left", "Alt+Right");
        yield return Page("audit", "audit.filter", "Puts the caret in the path or tool filter", "Ctrl+F", "Ctrl+F");
        yield return PageKeys("audit", "audit.rows", "Moves between events; Enter opens one", "Up, Down, Enter");
        yield return Page("memory", "memory.search", "Searches the facts", "Ctrl+F", "Ctrl+F");
        yield return Page("memory", "memory.tabs", "Picks a tab", "Ctrl+1 to Ctrl+5", "Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+5");
        yield return PageKeys("board", "board.move", "Moves between sessions; Enter attaches, Space peeks", "Arrows, Enter, Space");
        yield return PageKeys("rewind", "rewind.move", "Moves through the timeline", "Up, Down");
        yield return Page("skills", "skills.filter", "Puts the caret in the skills filter", "Ctrl+F", "Ctrl+F");
        yield return Page("skills", "skills.tabs", "Picks Review, Library, History or Import", "Ctrl+1 to Ctrl+4", "Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4");
        yield return PageKeys("skills", "skills.rows", "In the library or the history, opens the next or previous row", "Up, Down");
        yield return Page("skills", "skills.approve", "In a draft's editor, approves as edited", "Ctrl+Enter", "Ctrl+Enter");
        yield return PageKeys("skills", "skills.close", "Clears the filter, undoes an edit, or closes the open skill or commit", "Esc");
        yield return PageKeys("jobs", "jobs.rows", "Moves between jobs; Enter opens one", "Up, Down, Enter");
        yield return PageKeys("jobs", "jobs.close", "Closes an open question, then the form", "Esc");
        yield return PageKeys("mcp", "mcp.rows", "Moves between servers; Enter opens one", "Up, Down, Enter");
        yield return PageKeys("mcp", "mcp.close", "Closes an open question (remove, trust, forget)", "Esc");
        yield return PageKeys("gateway", "gateway.close", "Closes an open question, then the allowlist editor", "Esc");
        yield return PageKeys("safety", "safety.remove", "On a rule's Remove…, asks to remove it", "Delete");
        yield return PageKeys("safety", "safety.close", "Stops asking", "Esc");
        yield return PageKeys("secrets", "secrets.set", "In Set a secret…'s name field, starts the set", "Enter");
        yield return PageKeys("secrets", "secrets.close", "Closes the picker", "Esc");
    }
}
