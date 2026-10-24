using Corvid.App.Core;

namespace Corvid.App.Controls;

/// <summary>
/// The path data of every icon the ten mockups draw, copied from their <c>.dc.html</c> files (16 by 16, drawn with a 1.6
/// stroke) and rewritten as explicit path markup: circles and rounded rectangles as arcs, every command and number
/// spelled out. <see cref="IconKind.PanelRight"/> is drawn in the same style; the mockups have no dock toggle.
/// </summary>
internal static class IconPaths
{
    private static readonly Dictionary<IconKind, string> Data = new()
    {
        [IconKind.Logo] = "M 2 9 c 2 -4 5 -6 9 -6 l 3 1 l -2 1 c -1 3 -3 5 -6 6 l -1 2 l -1 -2 H 2 Z",
        [IconKind.Bell] = "M 4 11 V 7 a 4 4 0 0 1 8 0 v 4 l 1 1 H 3 Z M 6.5 13.5 a 1.5 1.5 0 0 0 3 0",
        [IconKind.Chat] = "M 2.5 3.5 h 11 v 7 h -7 l -3 2.5 v -2.5 h -1 Z",
        [IconKind.Board] = "M 2.5 2.5 h 4 v 6 h -4 Z M 9.5 2.5 h 4 v 3 h -4 Z M 9.5 8.5 h 4 v 5 h -4 Z M 2.5 11.5 h 4 v 2 h -4 Z",
        [IconKind.Sliders] = "M 4 2.5 v 11 M 2 6 h 4 M 10 2.5 v 11 M 12 10 H 8",
        [IconKind.Layers] = "M 8 2.5 l 5.5 3 L 8 8.5 l -5.5 -3 Z M 2.5 8.5 L 8 11.5 l 5.5 -3 M 2.5 11 L 8 14 l 5.5 -3",
        [IconKind.Sparkle] = "M 8 2 l 1.5 4 L 14 7.5 L 9.5 9 L 8 13 L 6.5 9 L 2 7.5 L 6.5 6 Z",
        [IconKind.Clock] = "M 2.5 8 A 5.5 5.5 0 1 1 13.5 8 A 5.5 5.5 0 1 1 2.5 8 Z M 8 5 v 3 l 2 1.5",
        [IconKind.Chip] = "M 5.5 4 H 10.5 A 1.5 1.5 0 0 1 12 5.5 V 10.5 A 1.5 1.5 0 0 1 10.5 12 H 5.5 A 1.5 1.5 0 0 1 4 10.5 V 5.5 "
            + "A 1.5 1.5 0 0 1 5.5 4 Z M 6.5 1.5 v 2.5 M 9.5 1.5 v 2.5 M 6.5 12 v 2.5 M 9.5 12 v 2.5 M 1.5 6.5 H 4 M 1.5 9.5 H 4 "
            + "M 12 6.5 h 2.5 M 12 9.5 h 2.5",
        [IconKind.Shield] = "M 8 1.5 l 5 2 v 4 c 0 3 -2.2 5.3 -5 6.5 c -2.8 -1.2 -5 -3.5 -5 -6.5 v -4 Z",
        [IconKind.Plug] = "M 5 2 v 4 M 11 2 v 4 M 3.5 6 h 9 v 2 a 4.5 4.5 0 0 1 -9 0 Z M 8 12.5 V 15",
        [IconKind.Send] = "M 14 2 L 2 7 l 5 2 l 2 5 Z",
        [IconKind.Lock] = "M 4.5 7 H 11.5 A 1.5 1.5 0 0 1 13 8.5 V 12.5 A 1.5 1.5 0 0 1 11.5 14 H 4.5 A 1.5 1.5 0 0 1 3 12.5 V 8.5 "
            + "A 1.5 1.5 0 0 1 4.5 7 Z M 5.5 7 V 5 a 2.5 2.5 0 0 1 5 0 v 2",
        [IconKind.Lines] = "M 3 3 h 10 M 3 6.5 h 10 M 3 10 h 6 M 3 13.5 h 4",
        [IconKind.Terminal] = "M 3 3 H 13 A 1.5 1.5 0 0 1 14.5 4.5 V 11.5 A 1.5 1.5 0 0 1 13 13 H 3 A 1.5 1.5 0 0 1 1.5 11.5 V 4.5 "
            + "A 1.5 1.5 0 0 1 3 3 Z M 4.5 6.5 l 2 1.5 l -2 1.5 M 8 10 h 3.5",
        [IconKind.Paperclip] = "M 10.5 4.5 L 5.8 9.2 a 1.7 1.7 0 0 0 2.4 2.4 l 5 -5 a 3.2 3.2 0 0 0 -4.5 -4.5 L 3.5 7.3 "
            + "a 4.6 4.6 0 0 0 6.5 6.5 l 3.5 -3.5",
        [IconKind.Question] = "M 2 8 A 6 6 0 1 1 14 8 A 6 6 0 1 1 2 8 Z M 6.2 6.2 a 1.8 1.8 0 1 1 2.6 1.6 c -0.6 0.3 -0.8 0.7 -0.8 1.2 "
            + "M 8 11.6 v 0.4",
        [IconKind.Warning] = "M 8 2 l 6.5 11.5 h -13 Z M 8 6.5 v 3 M 8 11.8 v 0.4",
        [IconKind.PopOut] = "M 6 3 H 3 v 10 h 10 v -3 M 9 3 h 4 v 4 M 13 3 L 7.5 8.5",
        [IconKind.Cloud] = "M 5 12.5 h 7 a 3 3 0 0 0 0.5 -6 A 4.5 4.5 0 0 0 4 7.5 A 2.5 2.5 0 0 0 5 12.5 Z",
        [IconKind.File] = "M 4 1.5 h 5 l 3 3 v 10 H 4 Z M 9 1.5 v 3 h 3",
        [IconKind.ChevronDown] = "M 4 6 l 4 4 l 4 -4",
        [IconKind.Search] = "M 2.5 7 A 4.5 4.5 0 1 1 11.5 7 A 4.5 4.5 0 1 1 2.5 7 Z M 10.5 10.5 L 14 14",
        [IconKind.Globe] = "M 2 8 A 6 6 0 1 1 14 8 A 6 6 0 1 1 2 8 Z M 2 8 h 12 M 8 2 c 2 2 2 10 0 12 M 8 2 c -2 2 -2 10 0 12",
        [IconKind.Menu] = "M 2.5 4 h 11 M 2.5 8 h 11 M 2.5 12 h 11",
        [IconKind.More] = "M 2.3 8 A 1.2 1.2 0 1 1 4.7 8 A 1.2 1.2 0 1 1 2.3 8 Z M 6.8 8 A 1.2 1.2 0 1 1 9.2 8 A 1.2 1.2 0 1 1 6.8 8 Z "
            + "M 11.3 8 A 1.2 1.2 0 1 1 13.7 8 A 1.2 1.2 0 1 1 11.3 8 Z",
        [IconKind.Check] = "M 3 8.5 l 3 3 l 7 -7",
        [IconKind.PanelRight] = "M 3 2.5 H 13 A 1.5 1.5 0 0 1 14.5 4 V 12 A 1.5 1.5 0 0 1 13 13.5 H 3 A 1.5 1.5 0 0 1 1.5 12 V 4 "
            + "A 1.5 1.5 0 0 1 3 2.5 Z M 10 2.5 v 11",
    };

    /// <summary>Every kind that draws something (all but <see cref="IconKind.None"/>).</summary>
    public static IEnumerable<IconKind> Kinds => Data.Keys;

    public static bool IsFilled(IconKind kind) => kind == IconKind.Logo;

    /// <summary>A new geometry for the kind, or null for <see cref="IconKind.None"/>. A geometry belongs to one path, so each icon parses its own.</summary>
    public static Geometry? Create(IconKind kind) =>
        Data.TryGetValue(kind, out var data) ? (Geometry)data : null;
}
