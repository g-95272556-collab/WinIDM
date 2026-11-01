using System.Windows.Threading;
using WinIDM.App.ViewModels;
using WinIDM.App.Views;

namespace WinIDM.App.Services;

/// <summary>
/// Shows the overlay window while downloads run and the switch (03a: idm_pref_show_window_overlay) is on, and
/// re-applies its look whenever an overlay setting changes. Also feeds the tray tooltip progress line.
/// </summary>
public sealed class OverlayController : IDisposable
{
    private readonly Composition _c;
    private readonly MainViewModel _vm;
    private readonly TrayService _tray;
    private readonly Action _openMain;
    private readonly Dispatcher _ui;
    private readonly Action<string> _settingsHandler;
    private OverlayWindow? _window;
    private int _active;

    public OverlayController(Composition composition, MainViewModel vm, TrayService tray, Action openMain)
    {
        _c = composition;
        _vm = vm;
        _tray = tray;
        _openMain = openMain;
        _ui = Dispatcher.CurrentDispatcher;

        _vm.ActiveCountChanged += OnActiveCount;
        _vm.TraySummaryChanged += OnSummary;
        _settingsHandler = key => _ui.BeginInvoke(new Action(() => OnSetting(key)));
        _c.Settings.Changed += _settingsHandler;
    }

    private void OnActiveCount(int active)
    {
        _active = active;
        Update();
    }

    private void OnSummary(string? summary)
    {
        _tray.SetDetail(_c.Settings.Bool(Keys.Notifications) ? summary : null);
        if (_window is { IsVisible: true }) _window.SetSummary(summary, OverallPercent());
    }

    private void OnSetting(string key)
    {
        if (key == Keys.Notifications) OnSummary(_vm.TraySummary);
        if (key.EndsWith("_overlay", StringComparison.Ordinal))
        {
            _window?.Apply(_c.Settings);
            Update();
        }
    }

    private double OverallPercent()
    {
        var rows = _vm.ActiveRowsView.Cast<DownloadRowViewModel>().Where(r => r.TotalBytes > 0).ToList();
        long total = rows.Sum(r => r.TotalBytes);
        return total > 0 ? rows.Sum(r => r.Item.DownloadedBytes) * 100.0 / total : 0;
    }

    private void Update()
    {
        bool show = _active > 0 && _c.Settings.Bool(Keys.Overlay);
        if (!show)
        {
            _window?.Hide();
            return;
        }

        if (_window is null)
        {
            _window = new OverlayWindow(_vm.ActiveRowsView);
            _window.OpenRequested += _openMain;
        }

        _window.Apply(_c.Settings);
        _window.SetSummary(_vm.TraySummary, OverallPercent());
        if (!_window.IsVisible) _window.Show();
    }

    public void Dispose()
    {
        _vm.ActiveCountChanged -= OnActiveCount;
        _vm.TraySummaryChanged -= OnSummary;
        _c.Settings.Changed -= _settingsHandler;
        _window?.Close();
        _window = null;
    }
}
