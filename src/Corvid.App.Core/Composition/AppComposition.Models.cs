using Corvid.App.Core.Models;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private ModelsViewModel? _models;

    /// <summary>The Models area: slots, the catalogue, downloads and placement.</summary>
    public ModelsViewModel Models => _models ??= new ModelsViewModel();
}
