using System.Windows;
using WinIDM.App.ViewModels;
using WinIDM.App.Views;
using WinIDM.Core.Models;

namespace WinIDM.App.Services;

/// <summary>
/// Interface used by view models to open windows and dialogs.
/// View models never reference a Window directly.
/// </summary>
public interface IShell
{
    void ShowMainWindow();
    /// <summary>Add a download: open the editor, or add it directly when "skip editor" is on.</summary>
    Task AddDownloadAsync(DownloadRequest? prefill);
    void OpenSettings();
    void OpenBrowser(bool incognito = false, string? url = null);
    void OpenTorrent(string? source = null);
    void OpenBackup();
    void OpenPasswords();
    void OpenSavedPages();
    void OpenThemeEditor();
    void OpenAbout();
    bool Confirm(string title, string message);
    string? Prompt(string title, string label, string? initial);
    void ShowText(string title, string text);
    void ShowMessage(string title, string message, bool error = false);
    void Exit();
}

public sealed class ShellService : IShell
{
    private readonly Composition _c;
    private Window? _main;
    private SettingsWindow? _settings;
    private TorrentWindow? _torrent;
    private BackupWindow? _backup;
    private BrowserWindow? _browser;
    private PasswordManagerWindow? _passwords;
    private SavedPagesWindow? _savedPages;

    public ShellService(Composition composition) => _c = composition;

    public Action? ExitAction { get; set; }

    public void AttachMain(Window main) => _main = main;

    private Window? Owner => _main is { IsVisible: true } ? _main : null;

    public void ShowMainWindow()
    {
        if (_main is null) return;
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Show();
        _main.Activate();
    }

    public async Task AddDownloadAsync(DownloadRequest? prefill)
    {
        var req = prefill ?? new DownloadRequest();
        bool skip = _c.Settings.Bool(Keys.SkipEditor) && UrlHelper.IsDownloadable(req.Url);
        if (!skip)
        {
            var dlg = new AddUrlWindow(new AddUrlViewModel(_c, req)) { Owner = Owner };
            if (dlg.ShowDialog() != true || dlg.Result is null) return;
            req = dlg.Result;
        }
        else
        {
            req.SaveDirectory ??= _c.DefaultDownloadFolder();
            req.FileName ??= UrlHelper.FileNameFromUrl(req.Url);
        }

        try
        {
            await _c.Engine.AddAsync(req);
        }
        catch (Exception ex)
        {
            AppLog.Error("Add download", ex);
            ShowMessage("Add download", "Could not add the download. See the log for details.", error: true);
        }
    }

    public void OpenSettings()
    {
        if (_settings is { IsLoaded: true } s) { s.Activate(); return; }
        _settings = new SettingsWindow(new SettingsViewModel(_c.Settings, _c.RawSettings)) { Owner = Owner };
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
    }

    public void OpenBrowser(bool incognito = false, string? url = null)
    {
        if (incognito)
        {
            // Each incognito window has its own temporary profile.
            var inc = new BrowserWindow(_c, this, incognito: true, url);
            inc.Show();
            return;
        }

        if (_browser is { IsLoaded: true } b)
        {
            if (b.WindowState == WindowState.Minimized) b.WindowState = WindowState.Normal;
            b.Activate();
            if (!string.IsNullOrWhiteSpace(url)) b.OpenUrlInNewTab(url);
            return;
        }
        _browser = new BrowserWindow(_c, this, incognito: false, url);
        _browser.Closed += (_, _) => _browser = null;
        _browser.Show();
    }

    public void OpenTorrent(string? source = null)
    {
        if (_torrent is { IsLoaded: true } t)
        {
            t.Activate();
            if (source is not null) t.SetSource(source);
            return;
        }
        _torrent = new TorrentWindow(new TorrentViewModel(_c, this, source)) { Owner = Owner };
        _torrent.Closed += (_, _) => _torrent = null;
        _torrent.Show();
    }

    public void OpenBackup()
    {
        if (_backup is { IsLoaded: true } b) { b.Activate(); return; }
        _backup = new BackupWindow(_c, this) { Owner = Owner };
        _backup.Closed += (_, _) => _backup = null;
        _backup.Show();
    }

    public void OpenThemeEditor()
    {
        var w = new ThemeEditorWindow(new ThemeEditorViewModel(_c.Settings)) { Owner = Owner };
        w.Show();
    }

    public void OpenAbout()
    {
        var w = new AboutWindow(_c) { Owner = Owner };
        w.ShowDialog();
    }

    public void OpenSavedPages()
    {
        if (_savedPages is { IsLoaded: true } s) { s.Activate(); return; }
        _savedPages = new SavedPagesWindow(_c) { Owner = Owner };
        _savedPages.Closed += (_, _) => _savedPages = null;
        _savedPages.Show();
    }

    public void OpenPasswords()
    {
        if (_passwords is { IsLoaded: true } p) { p.Activate(); return; }
        _passwords = new PasswordManagerWindow(_c, new PasswordManagerViewModel(_c, this)) { Owner = Owner };
        _passwords.Closed += (_, _) => _passwords = null;
        _passwords.Show();
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public string? Prompt(string title, string label, string? initial)
    {
        var dlg = new TextPromptWindow(title, label, initial ?? "") { Owner = Owner };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }

    public void ShowText(string title, string text)
    {
        new TextInfoWindow(title, text) { Owner = Owner }.Show();
    }

    public void ShowMessage(string title, string message, bool error = false) =>
        MessageBox.Show(Owner, message, title, MessageBoxButton.OK, error ? MessageBoxImage.Error : MessageBoxImage.Information);

    public void Exit() => ExitAction?.Invoke();
}
