using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace WinIDM.App.Services;

/// <summary>
/// System tray icon and balloon notifications. The icon is drawn programmatically (no third-party assets).
/// </summary>
/// <summary>15 tray quick actions (equivalent to the IDM+ Quick Actions group).</summary>
public enum QuickAction
{
    StartAll, StartScheduled, SmartDownload, ClipboardDownload, OpenApp, Browser, BrowserClipboard,
    Incognito, IncognitoClipboard, ExitApp, ExitAndPause, ExitSkipBackup, NewTab, NewIncognitoTab, ReadPage,
}

public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _statusItem;
    private Drawing.Icon? _drawn;

    public event Action? OpenRequested;
    public event Action? AddUrlRequested;
    public event Action? StartAllRequested;
    public event Action? PauseAllRequested;
    public event Action? BrowserRequested;
    public event Action? ExitRequested;
    public event Action? ThemeEditorRequested;
    public event Action? AboutRequested;
    /// <summary>Raised when one of the 15 quick actions is selected.</summary>
    public event Action<QuickAction>? QuickActionRequested;

    public static readonly (QuickAction Action, string Label)[] QuickActions =
    {
        (QuickAction.StartAll, "Start all downloads"),
        (QuickAction.StartScheduled, "Start scheduled downloads"),
        (QuickAction.SmartDownload, "Smart download (clipboard)"),
        (QuickAction.ClipboardDownload, "Download from clipboard"),
        (QuickAction.OpenApp, "Open application"),
        (QuickAction.Browser, "Browser"),
        (QuickAction.BrowserClipboard, "Browser + clipboard"),
        (QuickAction.Incognito, "Incognito"),
        (QuickAction.IncognitoClipboard, "Incognito + clipboard"),
        (QuickAction.ExitApp, "Exit application"),
        (QuickAction.ExitAndPause, "Exit after pausing all"),
        (QuickAction.ExitSkipBackup, "Exit without backup on close"),
        (QuickAction.NewTab, "New tab"),
        (QuickAction.NewIncognitoTab, "New incognito tab"),
        (QuickAction.ReadPage, "Read page"),
    };

    public TrayService()
    {
        _statusItem = new WinForms.ToolStripMenuItem("No active downloads") { Enabled = false };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Open WinIDM", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Add URL...", null, (_, _) => AddUrlRequested?.Invoke());
        menu.Items.Add("Start all", null, (_, _) => StartAllRequested?.Invoke());
        menu.Items.Add("Pause all", null, (_, _) => PauseAllRequested?.Invoke());
        menu.Items.Add("Browser", null, (_, _) => BrowserRequested?.Invoke());

        var quick = new WinForms.ToolStripMenuItem("Quick actions");
        foreach (var (action, label) in QuickActions)
        {
            var captured = action;
            quick.DropDownItems.Add(label, null, (_, _) => QuickActionRequested?.Invoke(captured));
            if (action == QuickAction.ExitSkipBackup) quick.DropDownItems.Add(new WinForms.ToolStripSeparator());
        }
        menu.Items.Add(quick);
        menu.Items.Add("Theme editor...", null, (_, _) => ThemeEditorRequested?.Invoke());
        menu.Items.Add("About WinIDM...", null, (_, _) => AboutRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _drawn = DrawIcon(false);
        _icon = new WinForms.NotifyIcon
        {
            Icon = _drawn,
            Text = "WinIDM",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public void SetStatus(int activeCount)
    {
        _statusItem.Text = activeCount == 0 ? "No active downloads" : $"Active downloads: {activeCount}";
        var text = activeCount == 0 ? "WinIDM" : $"WinIDM - {activeCount} active";
        _icon.Text = text.Length > 63 ? text[..63] : text; // the NotifyIcon text limit is 63 characters
    }

    /// <summary>Tray balloon notification. The message is trimmed to the Windows limit.</summary>
    public void Notify(string title, string text, bool error = false)
    {
        var t = title.Length > 63 ? title[..63] : title;
        var m = text.Length > 255 ? text[..255] : text;
        _icon.ShowBalloonTip(5000, t, m, error ? WinForms.ToolTipIcon.Error : WinForms.ToolTipIcon.Info);
    }

    private static Drawing.Icon DrawIcon(bool attention)
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            using var bg = new Drawing.SolidBrush(attention ? Drawing.Color.FromArgb(214, 84, 40) : Drawing.Color.FromArgb(47, 111, 222));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var fg = new Drawing.SolidBrush(Drawing.Color.White);
            g.FillRectangle(fg, 14, 7, 4, 12);
            g.FillPolygon(fg, new[] { new Drawing.Point(8, 16), new Drawing.Point(24, 16), new Drawing.Point(16, 26) });
        }
        var hIcon = bmp.GetHicon();
        var icon = (Drawing.Icon)Drawing.Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _drawn?.Dispose();
    }
}

/// <summary>
/// Watches the clipboard through WM_CLIPBOARDUPDATE with a 1 second debounce (R-46).
/// Only the first line is taken (R-47). Clips tagged CPFROM1DM are ignored.
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private readonly DispatcherTimer _debounce;
    private HwndSource? _source;
    private string? _lastSeen;

    public event Action<string>? UrlCaptured;

    public ClipboardMonitor()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); ReadClipboard(); };
    }

    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);
        AddClipboardFormatListener(helper.Handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            _debounce.Stop();
            _debounce.Start();
        }
        return IntPtr.Zero;
    }

    private void ReadClipboard()
    {
        try
        {
            if (Clipboard.ContainsData(ClipboardWriter.FromAppFormat)) return;
            if (!Clipboard.ContainsText()) return;
            var line = UrlHelper.FirstLine(Clipboard.GetText());
            if (line is null || line == _lastSeen) return;
            if (!UrlHelper.IsDownloadable(line)) return;
            _lastSeen = line;
            UrlCaptured?.Invoke(line);
        }
        catch (COMException)
        {
            // The clipboard is locked by another application; try again on the next update.
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public void Dispose()
    {
        _debounce.Stop();
        if (_source is not null)
        {
            RemoveClipboardFormatListener(_source.Handle);
            _source.RemoveHook(WndProc);
        }
    }
}
