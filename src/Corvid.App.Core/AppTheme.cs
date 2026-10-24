namespace Corvid.App.Core;

/// <summary>The colour theme: the platform's, or light or dark whatever the platform says (<c>--theme</c>, Settings).</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// The colour roles a tag, a badge or a row can take, one per Corvid concept (docs/plan/ui.md §7): amber for taint and
/// waiting, red for detection and denial, green for done, violet for escalation and jobs, blue for the accent, grey for
/// neutral. Never the only carrier of meaning: the text says it too.
/// </summary>
public enum Tone
{
    Grey,
    Amber,
    Red,
    Green,
    Violet,
    Blue,
}
