using Corvid.App.Core.Gateway;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private GatewayViewModel? _gateway;

    /// <summary>The Gateway area: the chat adapters and pairing.</summary>
    public GatewayViewModel Gateway => _gateway ??= new GatewayViewModel();
}
