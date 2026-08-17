using System.Diagnostics;
using PairUp.App.Audio;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace PairUp.App.Services;

/// <summary>
/// Owns the tray (notification area) icon and its right-click device menu, so devices can be
/// connected/disconnected without the main window being open. Uses WinForms' NotifyIcon since
/// WPF has no built-in equivalent; this runs fine inside a WPF app because the Dispatcher's
/// Win32 message pump also services the icon's hidden window.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Func<IEnumerable<AudioDeviceInfo>> _getDevices;
    private readonly Action<AudioDeviceInfo> _toggleDevice;

    public TrayIconService(
        Func<IEnumerable<AudioDeviceInfo>> getDevices,
        Action<AudioDeviceInfo> toggleDevice,
        Action showWindow,
        Action exitApp)
    {
        _getDevices = getDevices;
        _toggleDevice = toggleDevice;

        Drawing.Icon icon;
        try
        {
            icon = Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule!.FileName!)
                   ?? Drawing.SystemIcons.Application;
        }
        catch
        {
            icon = Drawing.SystemIcons.Application;
        }

        _icon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "PairUp",
            Visible = false,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu(showWindow, exitApp);
        _icon.DoubleClick += (_, _) => showWindow();
    }

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(2000, title, text, Forms.ToolTipIcon.None);

    private void BuildMenu(Action showWindow, Action exitApp)
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();

        var devices = _getDevices().ToList();
        if (devices.Count == 0)
        {
            menu.Items.Add(new Forms.ToolStripMenuItem("No devices found") { Enabled = false });
        }
        else
        {
            foreach (var device in devices)
            {
                var item = new Forms.ToolStripMenuItem(device.Name) { Checked = device.IsConnected };
                item.Click += (_, _) => _toggleDevice(device);
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Forms.ToolStripSeparator());

        var openItem = new Forms.ToolStripMenuItem("Open PairUp");
        openItem.Click += (_, _) => showWindow();
        menu.Items.Add(openItem);

        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => exitApp();
        menu.Items.Add(exitItem);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
