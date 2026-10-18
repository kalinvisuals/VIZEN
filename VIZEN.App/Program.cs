using VIZEN.App.Services;

namespace VIZEN.App;

/// <summary>
/// Entry point. Started by Chrome / Edge / Brave for the VIZEN Connector extension (first argument
/// "chrome-extension://…/"), VIZEN runs only the small native messaging relay: no window, no WPF. Otherwise the app.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (BrowserNativeHost.IsHostLaunch(args))
        {
            return BrowserNativeHost.Run();
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
