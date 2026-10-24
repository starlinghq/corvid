using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Palette;

/// <summary>What the palette is showing: one surface with a list, opened by a different key each (docs/plan/ui.md §6).</summary>
public enum PaletteMode
{
    /// <summary>Ctrl+K, Ctrl+P: every command, view and setting.</summary>
    Commands,

    /// <summary>Ctrl+Tab: the session switcher.</summary>
    Sessions,

    /// <summary>Ctrl+R: the workspace's prompt history.</summary>
    History,

    /// <summary>Ctrl+F: find in the open session.</summary>
    Find,

    /// <summary>Ctrl+Shift+F: search across sessions.</summary>
    Search,

    /// <summary>F1, ?: help and the shortcuts.</summary>
    Help,

    /// <summary>Alt+P: switch the generator without leaving the composer.</summary>
    Generator,

    /// <summary>A key whose feature is not built yet (Ctrl+N, Ctrl+Shift+N, Ctrl+;): its surface, saying what it comes with.</summary>
    Pending,
}

/// <summary>
/// One row of the palette: its title and detail (already cleaned of hidden characters), the group it is listed under, the
/// key that does the same, and what choosing it does. A row that cannot be chosen says why in <see cref="Note"/>.
/// </summary>
public sealed class PaletteItem : ObservableObject
{
    private bool _isSelected;
    private string _groupHeader = "";

    public PaletteItem(string title, string detail, Action? run, string group = "", string keyText = "", IconKind icon = IconKind.None)
    {
        Title = title;
        Detail = detail;
        Run = run;
        Group = group;
        KeyText = keyText;
        Icon = icon;
    }

    public string Title { get; }

    public string Detail { get; }

    public string Group { get; }

    /// <summary>The key that does the same ("Ctrl+B"), drawn as key caps; empty for none.</summary>
    public string KeyText { get; }

    /// <summary>The key as the hint line draws it: caps for keys, plain text for "to" and commas.</summary>
    public IReadOnlyList<HintPart> KeyParts => Keys.KeyParts.For(KeyText);

    public IconKind Icon { get; }

    /// <summary>What choosing the row does; null for a row that cannot be chosen.</summary>
    public Action? Run { get; }

    /// <summary>Why the row cannot be chosen, or what it comes with ("comes with the engine (A3)").</summary>
    public string Note { get; init; } = "";

    /// <summary>Text matched but not shown (a session's id, a command's slash name).</summary>
    public string Keywords { get; init; } = "";

    /// <summary>The palette's choose command, with the row as its parameter: a click runs the row or shows why it cannot.</summary>
    public System.Windows.Input.ICommand? Choose { get; internal set; }

    public bool IsEnabled => Run is not null;

    public bool HasNote => Note.Length > 0;

    public bool HasDetail => Detail.Length > 0;

    public bool HasKey => KeyText.Length > 0;

    /// <summary>The group's heading when this row starts a group in the list as drawn; empty otherwise.</summary>
    public string GroupHeader
    {
        get => _groupHeader;
        set
        {
            if (SetProperty(ref _groupHeader, value ?? ""))
                OnPropertyChanged(nameof(HasGroupHeader));
        }
    }

    public bool HasGroupHeader => _groupHeader.Length > 0;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string AutomationName
    {
        get
        {
            var parts = new List<string> { Title };
            if (Detail.Length > 0)
                parts.Add(Detail);
            if (KeyText.Length > 0)
                parts.Add(KeyText);
            if (Note.Length > 0)
                parts.Add(Note);
            return string.Join(", ", parts);
        }
    }
}
