using System;
using QuickMail.Services;

namespace QuickMail.Views;

/// <summary>
/// Owns the system-tray <c>NotifyIcon</c> used by close-to-tray mode. WPF has no native tray icon,
/// so this is the one place WinForms is used (its types are always fully qualified — see the
/// UseWindowsForms note in QuickMail.csproj). Purely UI plumbing (a View-layer concern): it exposes
/// Show/Hide and raises the supplied callbacks for the Open and Exit menu actions. The callbacks
/// fire on the UI thread (the NotifyIcon is created on it).
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;

    public TrayIconManager(Action onOpen, Action onExit)
    {
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text    = "QuickMail",
            Icon    = LoadAppIcon(),
            Visible = false,
        };
        // Double-clicking the icon is the conventional "restore" gesture.
        _icon.DoubleClick += (_, _) => onOpen();

        var menu     = new System.Windows.Forms.ContextMenuStrip();
        var openItem = new System.Windows.Forms.ToolStripMenuItem("&Open QuickMail");
        openItem.Click += (_, _) => onOpen();
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("E&xit QuickMail");
        exitItem.Click += (_, _) => onExit();
        menu.Items.Add(openItem);
        menu.Items.Add(exitItem);
        _icon.ContextMenuStrip = menu;
    }

    public void Show() => _icon.Visible = true;
    public void Hide() => _icon.Visible = false;

    // The app ships no dedicated .ico, so use the executable's own icon; fall back to the generic
    // application icon if it can't be read. Picks up a real app icon automatically once one is set.
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"TrayIconManager: could not load app icon: {ex.Message}");
        }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false; // remove from the tray immediately
        _icon.Dispose();
    }
}
