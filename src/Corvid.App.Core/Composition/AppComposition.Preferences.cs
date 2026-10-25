using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    /// <summary>The layout preferences read and applied (engine mode); complete at once in demo mode. It never faults.</summary>
    public Task PreferencesApplied { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The window follows the layout preferences kept in ui.db (G-7, Settings › Layout) when it starts: the rail or the dock
    /// hidden, "Focus" (the conversation without the dock) or "Board" (the Board first, unless <c>--page</c> named an area).
    /// The engine's only: the demo writes no preferences. A failure to read them shows as a status bar tag.
    /// </summary>
    private async Task ApplyPreferencesAsync()
    {
        try
        {
            var values = (await SettingsService.LoadAsync().ConfigureAwait(true)).Values;
            bool On(string key) => values.TryGetValue(key, out var text) && bool.TryParse(text, out var on) && on;
            var layout = values.TryGetValue("StartLayout", out var start) ? start : "Work";
            if (On("RailCollapsed") || On("DockCollapsed") || layout == "Focus")
                Shell.ApplyStartLayout(On("RailCollapsed"), On("DockCollapsed") || layout == "Focus");
            if (layout == "Board" && Options.Page is null)
                Shell.Navigate(Area.Board);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            StatusBar.Tags = [.. StatusBar.Tags, new StatusTag("preferences not read", Tone.Amber,
                $"The window's layout preferences could not be read: {SafeText.Clean(exception.Message)}")];
        }
    }
}
