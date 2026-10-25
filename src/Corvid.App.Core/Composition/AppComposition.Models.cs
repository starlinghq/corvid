using Corvid.App.Core.Models;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private IModelService? _modelService;
    private ModelsViewModel? _models;

    /// <summary>
    /// The model store the Models page and the first run share, so a download started on the first run shows its progress
    /// on Models. Demo mode (the engine's comes from <see cref="Services"/> instead) draws Models.png's moment, or, when the window
    /// opens on the first run (<c>--page first-run</c>), FirstRun.png's fresh machine with two default models missing.
    /// </summary>
    public IModelService ModelService => _modelService ??= Services.CreateModelService(this);

    /// <summary>The Models area: slots, the catalogue, downloads and placement.</summary>
    public ModelsViewModel Models => _models ??= new ModelsViewModel(ModelService, Shell, StatusBar);
}
