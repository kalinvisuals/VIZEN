using System.Windows;
using VIZEN.App.Localization;
using Forms = System.Windows.Forms;

namespace VIZEN.App.Views;

/// <summary>
/// VIZEN's icon in the notification area: double-click opens VIZEN; the menu shows/hides all overlays and media
/// layers or exits. Used when "Keep running in the notification area" is on.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _open;
    private readonly Forms.ToolStripMenuItem _toggle;
    private readonly Forms.ToolStripMenuItem _exit;

    public TrayIcon(Action open, Action toggleLayers, Action exit)
    {
        _open = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => open());
        _toggle = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => toggleLayers());
        _exit = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => exit());
        _open.Font = new System.Drawing.Font(_open.Font, System.Drawing.FontStyle.Bold);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([_open, _toggle, new Forms.ToolStripSeparator(), _exit]);
        menu.Opening += (_, _) => RefreshTexts();

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "VIZEN",
            ContextMenuStrip = menu,
            Visible = false,
        };
        _icon.DoubleClick += (_, _) => open();
        RefreshTexts();
    }

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    /// <summary>A short notification next to the icon (e.g. "VIZEN is still running").</summary>
    public void Notify(string title, string text) => _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    private void RefreshTexts()
    {
        _open.Text = Loc.T("Tray.Open");
        _toggle.Text = Loc.T("Tray.ToggleLayers");
        _exit.Text = Loc.T("Tray.Exit");
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/vizen.ico"));
        return resource is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(resource.Stream);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
