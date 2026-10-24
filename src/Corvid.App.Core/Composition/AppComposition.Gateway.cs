using Corvid.App.Core.Gateway;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private IGatewayService? _gatewayService;
    private GatewayViewModel? _gateway;

    /// <summary>
    /// The Gateway area's service: adapters, pairing, paired users and deliveries. Demo mode (the only mode until
    /// Corvid.App.Engine exists) holds the Board's Telegram pairing; it follows the Board's service, so a pairing answered in
    /// either place is answered once.
    /// </summary>
    public IGatewayService GatewayService => _gatewayService ??= new DemoGatewayService(Time, BoardService);

    /// <summary>The Gateway area: pairing, paired users, channels and deliveries.</summary>
    public GatewayViewModel Gateway => _gateway ??= new GatewayViewModel(GatewayService, Shell, StatusBar, Time);
}
