using System.Collections;

namespace Corvid.App.Infrastructure;

/// <summary>
/// Small functions for <c>x:Bind</c>, so views turn view-model values into visibility and text without converters. Each
/// name says what it tests; x:Bind picks functions by name, so none is overloaded.
/// </summary>
public static class Ui
{
    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfBoth(bool first, bool second) => first && second ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Visible when the text is not empty: status bar items, badges and subtitles that only sometimes exist.</summary>
    public static Visibility VisibleIfText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Visible when the list has items, so an empty list leaves no gap in a spaced row.</summary>
    public static Visibility VisibleIfAny(IEnumerable? items) =>
        items is not null && items.GetEnumerator().MoveNext() ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The mockups' section titles are upper case by CSS; the text stays as written for screen readers.</summary>
    public static string Upper(string? text) => (text ?? "").ToUpperInvariant();
}
