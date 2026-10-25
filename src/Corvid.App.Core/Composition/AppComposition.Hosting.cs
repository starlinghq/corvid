using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Composition;

public sealed partial class AppComposition
{
    private IHostingService? _hostingService;

    /// <summary>
    /// The app's hosting of the gateway and the scheduler (U-8): off until the person turns it on. Made with the shell, so the
    /// status bar's notice shows whenever this app hosts either, whatever page is open.
    /// </summary>
    public IHostingService HostingService
    {
        get
        {
            if (_hostingService is not null)
                return _hostingService;
            _hostingService = Services.CreateHostingService(this);
            _hostingService.Changed += OnHostingChanged;
            OnHostingChanged(this, EventArgs.Empty);
            return _hostingService;
        }
    }

    private void OnHostingChanged(object? sender, EventArgs e) => StatusBar.Hosting = _hostingService?.State.Notice ?? "";

    private void DisposeHosting()
    {
        if (_hostingService is null)
            return;
        _hostingService.Changed -= OnHostingChanged;
        (_hostingService as IDisposable)?.Dispose();
    }
}
