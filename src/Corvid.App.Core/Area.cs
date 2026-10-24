namespace Corvid.App.Core;

/// <summary>
/// The app's destinations (docs/plan/ui.md §3). Each has a folder in this project (its service interface, DTOs, demo
/// service and view model) and a page in Corvid.App (<c>Pages/&lt;Area&gt;Page.xaml</c>), and a file of its own in
/// <see cref="Composition.AppComposition"/>.
/// </summary>
public enum Area
{
    Sessions,
    Board,
    Review,
    Rewind,
    Memory,
    Skills,
    Jobs,
    Models,
    Safety,
    Mcp,
    Gateway,
    Secrets,
    Audit,
    Settings,
    FirstRun,
}

public static class Areas
{
    /// <summary>
    /// The rail's destinations, in the mockups' order. Rewind opens from a session, and Settings and the doctor (the first
    /// run's checks) from the rail's footer, so none of them has a row.
    /// </summary>
    public static IReadOnlyList<Area> Rail { get; } =
    [
        Area.Sessions, Area.Board, Area.Review, Area.Memory, Area.Skills, Area.Jobs, Area.Models, Area.Safety, Area.Mcp,
        Area.Gateway, Area.Secrets, Area.Audit,
    ];

    /// <summary>The name a person reads: the rail's label and the page's title.</summary>
    public static string Title(this Area area) => area switch
    {
        Area.Mcp => "MCP",
        Area.FirstRun => "First run",
        _ => area.ToString(),
    };

    public static IconKind Icon(this Area area) => area switch
    {
        Area.Sessions => IconKind.Chat,
        Area.Board => IconKind.Board,
        Area.Review => IconKind.Sliders,
        Area.Rewind => IconKind.Clock,
        Area.Memory => IconKind.Layers,
        Area.Skills => IconKind.Sparkle,
        Area.Jobs => IconKind.Clock,
        Area.Models => IconKind.Chip,
        Area.Safety => IconKind.Shield,
        Area.Mcp => IconKind.Plug,
        Area.Gateway => IconKind.Send,
        Area.Secrets => IconKind.Lock,
        Area.Audit => IconKind.Lines,
        _ => IconKind.None,
    };

    /// <summary>
    /// The rail row that is current while the area shows: Rewind is part of a session, so Sessions stays selected (as the
    /// Rewind mockup draws it). Settings and the first run have no row.
    /// </summary>
    public static Area? RailRow(this Area area) => area switch
    {
        Area.Rewind => Area.Sessions,
        Area.Settings or Area.FirstRun => null,
        _ => area,
    };

    /// <summary>Reads an area from the command line: its name or title in any case, with or without dashes (<c>first-run</c>).</summary>
    public static bool TryParse(string? text, out Area area)
    {
        var key = (text ?? "").Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal);
        foreach (var candidate in Enum.GetValues<Area>())
        {
            if (string.Equals(candidate.ToString(), key, StringComparison.OrdinalIgnoreCase))
            {
                area = candidate;
                return true;
            }
        }

        area = default;
        return false;
    }
}
