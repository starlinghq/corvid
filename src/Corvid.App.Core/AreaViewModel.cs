using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core;

/// <summary>
/// What every area's view model has: which area it is and the title its page shows. Each area's view model adds the
/// rest. Until an area is built, its page shows the title and <see cref="Summary"/>, one line on what will be there.
/// </summary>
public abstract class AreaViewModel(Area area, string summary) : ObservableObject
{
    public Area Area { get; } = area;

    public string Title => Area.Title();

    public string Summary { get; } = summary;
}
