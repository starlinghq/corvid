using Microsoft.UI.Xaml.Media;

namespace Corvid.App.Infrastructure;

/// <summary>
/// The theme's brushes for views built in code. Uno does not repaint a <c>Run</c>'s theme brush when the theme changes,
/// so code that colours runs looks the brush up for the element's current theme and rebuilds on
/// <c>ActualThemeChanged</c>.
/// </summary>
public static class ThemeBrushes
{
    /// <summary>The brush named <paramref name="key"/> (a corvid.css token, such as <c>CorvidAccent</c>) in the element's theme.</summary>
    public static Brush Get(FrameworkElement element, string key)
    {
        ArgumentNullException.ThrowIfNull(element);
        var theme = element.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        foreach (var dictionary in Dictionaries(Application.Current.Resources))
        {
            if (dictionary.ThemeDictionaries.TryGetValue(theme, out var themed) && themed is ResourceDictionary resources
                && resources.TryGetValue(key, out var value) && value is Brush brush)
                return brush;
        }

        return (Brush)Application.Current.Resources[key];
    }

    private static IEnumerable<ResourceDictionary> Dictionaries(ResourceDictionary root)
    {
        yield return root;
        foreach (var merged in root.MergedDictionaries)
        {
            foreach (var dictionary in Dictionaries(merged))
                yield return dictionary;
        }
    }
}
