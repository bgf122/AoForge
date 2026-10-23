using System;
using System.Windows;
using AoForge.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace AoForge;

// Minimizing sends AoForge to the notification area by the clock: it leaves the taskbar, keeps
// checking for a new AoForge, and says so with a notification. Closing the window still exits.
public partial class MainWindow
{
    Forms.NotifyIcon _tray;
    bool _trayHintShown;

    void CreateTray()
    {
        Drawing.Icon icon;
        try {
            icon = Drawing.Icon.ExtractAssociatedIcon(SelfUpdater.ExePath);
        } catch (Exception) {
            icon = Drawing.SystemIcons.Application;
        }
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open AoForge", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Check for updates", null, async (_, _) => await RefreshAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());

        _tray = new Forms.NotifyIcon { Icon = icon, Text = "AoForge", ContextMenuStrip = menu, Visible = false };
        _tray.MouseClick += (_, e) => {
            if (e.Button == Forms.MouseButtons.Left)
                RestoreFromTray();
        };
        _tray.BalloonTipClicked += (_, _) => RestoreFromTray();
        Closed += (_, _) => _tray.Dispose(); // or the icon lingers until the mouse passes over it
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState != WindowState.Minimized || _tray == null)
            return;
        Hide();
        _tray.Visible = true;
        if (!_trayHintShown) {
            _trayHintShown = true;
            _tray.ShowBalloonTip(4000, "AoForge", "AoForge is still running here and keeps checking for updates. Click to open it.", Forms.ToolTipIcon.Info);
        }
    }

    void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _tray.Visible = false;
    }

    // The Windows title bar in the app's colours: dark mode on Windows 10 (20H1+) and 11, and on
    // Windows 11 the exact caption, text and border colours. Older Windows ignore the attributes.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        int caption = 0x001D1816, text = 0x00EDEAE8, border = 0x00382E2A; // COLORREF (0x00BBGGRR): Panel, Text, Line
        DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36 /* DWMWA_TEXT_COLOR */, ref text, sizeof(int));
        DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref border, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // A new AoForge was found: tell the user if the window is in the tray.
    void NotifyTray(string text)
    {
        if (_tray != null && _tray.Visible)
            _tray.ShowBalloonTip(6000, "AoForge", text, Forms.ToolTipIcon.Info);
    }
}
