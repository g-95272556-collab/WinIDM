using System.IO;
using System.Windows;
using System.Windows.Shell;
using WinIDM.App.Services;
using WinIDM.App.ViewModels;
using WinIDM.App.Views;
using WinIDM.Core.Models;

namespace WinIDM.App;

public partial class App : Application
{
    private Composition? _c;
    private ShellService? _shell;
    private MainViewModel? _vm;
    private MainWindow? _main;
    private TrayService? _tray;
    private ClipboardMonitor? _clipboard;
    private bool _exiting;
    private bool _skipBackupOnExit;
    private SingleInstanceServer? _server;
    private PostDownloadCoordinator? _post;

    public App()
    {
        DispatcherUnhandledException += OnUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args ?? Array.Empty<string>();
        var dataDir = ArgValue(args, "--data-dir") ?? Composition.DefaultDataDir();

        // Single instance only: hand the arguments to the running instance and exit.
        if (!args.Contains("--smoke-test") && SingleInstance.TrySendToRunning(args))
        {
            Shutdown(0);
            return;
        }

        try
        {
            _c = Composition.Create(dataDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not open the data folder: {dataDir}", "WinIDM", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        AppLog.Init(_c.LogDir);
        AppLog.CaptureEnabled = () => _c.Settings.Bool(Keys.CaptureLog) || _c.Settings.Bool(Keys.DownloadDiagnostics);

        if (args.Contains("--smoke-test"))
        {
            // Build every view without showing it, then exit with code 0.
            RunSmokeTest();
            return;
        }

        MigrateThemeChoice(_c);
        ThemeService.Apply(_c.Settings.Text(Keys.Theme), _c.Settings.Text(Keys.CustomTheme));
        ApplyJumpList();

        _shell = new ShellService(_c) { ExitAction = ExitApp };
        _vm = new MainViewModel(_c, _shell);
        _main = new MainWindow(_vm, _shell, _c);
        _shell.AttachMain(_main);
        _main.Closing += OnMainClosing;

        _tray = new TrayService();
        _tray.OpenRequested += () => _shell.ShowMainWindow();
        _tray.AddUrlRequested += () => _ = _shell.AddDownloadAsync(null);
        _tray.StartAllRequested += () => _vm.StartAllCommand.Execute(null);
        _tray.PauseAllRequested += () => _vm.PauseAllCommand.Execute(null);
        _tray.BrowserRequested += () => _shell.OpenBrowser();
        _tray.ExitRequested += ExitApp;
        _tray.ThemeEditorRequested += () => _shell.OpenThemeEditor();
        _tray.AboutRequested += () => _shell.OpenAbout();
        _tray.QuickActionRequested += RunQuickAction;

        _vm.ActiveCountChanged += n => _tray.SetStatus(n);
        _vm.NotifyRequested += (title, text, error) =>
        {
            if (!_c.Settings.Bool(Keys.DisableAllToasts)) _tray.Notify(title, text, error);
        };

        _clipboard = new ClipboardMonitor();
        _clipboard.UrlCaptured += OnClipboardUrl;
        _main.SourceInitialized += (_, _) => _clipboard.Attach(_main);

        // Engine events: completed/error toasts and post-download actions (with confirmation).
        _post = new PostDownloadCoordinator(_c, _shell, (t, x, err) => _tray.Notify(t, x, err), ExitApp);
        _post.Attach();

        MainWindow = _main;
        _main.Show();
        if (args.Contains("--minimized")) _main.Hide();

        _ = InitializeAsync();
        _server = new SingleInstanceServer();
        _server.ArgumentsReceived += forwarded => Dispatcher.BeginInvoke(() => OnForwardedArguments(forwarded));
        _server.Start();
        HandleArguments(args);
    }

    /// <summary>Rewrite a theme choice stored by an older build (Malay names) to the current English name.</summary>
    private static void MigrateThemeChoice(Composition composition)
    {
        var stored = composition.Settings.Text(Keys.Theme);
        if (string.IsNullOrWhiteSpace(stored)) return;
        var normalized = ThemeService.NormalizeChoice(stored);
        if (!string.Equals(stored, normalized, StringComparison.Ordinal))
            composition.Settings.Set(Keys.Theme, normalized);
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _vm!.InitializeAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup list", ex);
        }

        // Backup scheduler: built from settings; the "app opened" backup runs once.
        try
        {
            _c!.BackupAutomation.Reload();
            _c.BackupAutomation.Start();
            await _c.BackupAutomation.OnAppOpenedAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Backup scheduler", ex);
        }
    }

    /// <summary>Arguments: URL, magnet:, .torrent file, or the 1dmdownload/idmdownload protocol.</summary>
    private void HandleArguments(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            if (raw.StartsWith("--"))
            {
                if (raw is "--data-dir") i++;
                RunCommand(raw);
                continue;
            }

            var candidate = UrlHelper.ExtractFromArgument(raw);
            if (candidate is null) continue;

            if (UrlHelper.IsDownloadable(candidate))
            {
                _ = _shell!.AddDownloadAsync(new DownloadRequest { Url = candidate, SaveDirectory = _c!.DefaultDownloadFolder() });
            }
            else if (candidate.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
            {
                _shell!.OpenTorrent(candidate);
            }
        }
    }

    /// <summary>Arguments from the second instance (already on the UI thread): show the main window and process them.</summary>
    private void OnForwardedArguments(string[] args)
    {
        if (_shell is null) return;
        _shell.ShowMainWindow();
        HandleArguments(args);
    }

    /// <summary>Jump List and tray commands: quick actions rather than URLs.</summary>
    private void RunCommand(string arg)
    {
        if (_shell is null || _c is null) return;
        switch (arg)
        {
            case "--browser-new-tab":
                _shell.OpenBrowser(false, null);
                break;
            case "--browser-incognito":
                _shell.OpenBrowser(incognito: true);
                break;
            case "--settings":
                _shell.OpenSettings();
                break;
            case "--add-from-clipboard":
                var text = UrlHelper.FirstLine(TryGetClipboardText());
                if (text is not null && UrlHelper.IsDownloadable(text))
                    _ = _shell.AddDownloadAsync(new DownloadRequest { Url = text, SaveDirectory = _c.DefaultDownloadFolder() });
                break;
        }
    }

    /// <summary>15 tray quick actions. Exit actions do not skip the system check (no system action here).</summary>
    private void RunQuickAction(QuickAction action)
    {
        if (_shell is null || _c is null || _vm is null) return;
        switch (action)
        {
            case QuickAction.StartAll:
                _vm.StartAllCommand.Execute(null);
                break;
            case QuickAction.StartScheduled:
                if (!_c.Settings.Bool("scheduler.enabled"))
                {
                    _tray?.Notify("WinIDM", "The download scheduler is off. Turn it on in the Scheduler tab.", false);
                    break;
                }
                _vm.StartAllCommand.Execute(null);
                break;
            case QuickAction.SmartDownload:
            case QuickAction.ClipboardDownload:
                AddFromClipboard();
                break;
            case QuickAction.OpenApp:
                _shell.ShowMainWindow();
                break;
            case QuickAction.Browser:
                _shell.OpenBrowser();
                break;
            case QuickAction.BrowserClipboard:
                _shell.OpenBrowser(false, ClipboardHttpUrl());
                break;
            case QuickAction.Incognito:
                _shell.OpenBrowser(incognito: true);
                break;
            case QuickAction.IncognitoClipboard:
                _shell.OpenBrowser(incognito: true, url: ClipboardHttpUrl());
                break;
            case QuickAction.ExitApp:
                ExitApp();
                break;
            case QuickAction.ExitAndPause:
                _vm.PauseAllCommand.Execute(null);
                ExitApp();
                break;
            case QuickAction.ExitSkipBackup:
                _skipBackupOnExit = true;
                ExitApp();
                break;
            case QuickAction.NewTab:
                _shell.OpenBrowser(false, null);
                break;
            case QuickAction.NewIncognitoTab:
                _shell.OpenBrowser(incognito: true);
                break;
            case QuickAction.ReadPage:
                // Reader mode lives in the browser; a direct link from the App project is out of scope.
                _shell.OpenBrowser(false, null);
                _tray?.Notify("Read page", "The browser is open. Use reader mode from the browser menu.", false);
                break;
        }
    }

    private void AddFromClipboard()
    {
        var url = UrlHelper.FirstLine(TryGetClipboardText());
        if (url is null || !UrlHelper.IsDownloadable(url))
        {
            _tray?.Notify("WinIDM", "The clipboard does not contain a download link.", false);
            return;
        }
        _ = _shell!.AddDownloadAsync(new DownloadRequest { Url = url, SaveDirectory = _c!.DefaultDownloadFolder() });
    }

    private string? ClipboardHttpUrl()
    {
        var line = UrlHelper.FirstLine(TryGetClipboardText());
        return UrlHelper.IsHttp(line) ? line : null;
    }

    private static string? TryGetClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (System.Runtime.InteropServices.ExternalException) { return null; }
    }

    /// <summary>Register the Jump List (New tab, New incognito, Settings, Add from clipboard).</summary>
    private void ApplyJumpList()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        JumpTask MakeTask(string title, string description, string argument) => new()
        {
            Title = title,
            Description = description,
            ApplicationPath = exe,
            Arguments = argument,
            IconResourcePath = exe,
            IconResourceIndex = 0,
        };

        var list = new JumpList { ShowRecentCategory = false };
        list.JumpItems.Add(MakeTask("New tab", "Open the browser with a new page", "--browser-new-tab"));
        list.JumpItems.Add(MakeTask("New incognito window", "Open the browser in incognito mode", "--browser-incognito"));
        list.JumpItems.Add(MakeTask("Settings", "Open WinIDM settings", "--settings"));
        list.JumpItems.Add(MakeTask("Add from clipboard", "Add the clipboard link as a download", "--add-from-clipboard"));
        JumpList.SetJumpList(this, list);
        list.Apply();
    }

    /// <summary>Clipboard filter: extensions are excluded, and "catch all" accepts web pages.</summary>
    private void OnClipboardUrl(string url)
    {
        if (_c is null || !_c.Settings.Bool(Keys.SmartDownload)) return;
        if (!UrlHelper.IsMagnet(url) && !_c.Settings.Bool(Keys.SmartDownloadAll) && UrlHelper.ExtensionOf(url) is null) return;

        var ext = UrlHelper.ExtensionOf(url);
        var excluded = _c.Settings.Text(Keys.SmartExcludeExt)
            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.TrimStart('.').ToLowerInvariant());
        if (ext is not null && excluded.Contains(ext)) return;

        _ = _shell!.AddDownloadAsync(new DownloadRequest { Url = url, SaveDirectory = _c.DefaultDownloadFolder() });
    }

    private void OnMainClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting) return;
        if (_c is not null && _c.Settings.Bool(Keys.MinimizeToTray) && _tray is not null)
        {
            e.Cancel = true;
            _main?.Hide();
            return;
        }
        ExitApp();
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        if (_c is not null && !_skipBackupOnExit)
        {
            // The "app closing" backup is time limited: the UI never hangs for more than 30 seconds.
            try
            {
                _c.BackupAutomation.OnAppClosingAsync().Wait(TimeSpan.FromSeconds(30));
            }
            catch (AggregateException ex)
            {
                AppLog.Error("Backup on app close", ex.InnerException ?? ex);
            }
        }
        _c?.BackupAutomation.Dispose();
        _post?.Dispose();
        _main?.Close();
        _clipboard?.Dispose();
        _server?.Dispose();
        _tray?.Dispose();
        _vm?.Dispose();
        Shutdown(0);
    }

    private void RunSmokeTest()
    {
        var c = _c!;
        ThemeService.Apply("System");
        var shell = new ShellService(c);
        var vm = new MainViewModel(c, shell);
        _ = new MainWindow(vm, shell, c);
        _ = new SettingsWindow(new SettingsViewModel(c.Settings, c.RawSettings));
        _ = new AddUrlWindow(new AddUrlViewModel(c, new DownloadRequest { Url = "https://example.com/file.zip" }));
        _ = new BackupWindow(c, shell);
        _ = new TorrentWindow(new TorrentViewModel(c, shell, null));
        _ = new PasswordManagerWindow(c, new PasswordManagerViewModel(c, shell));
        _ = new PinDialogWindow(PinDialogMode.Setup);
        _ = new SavedPagesWindow(c);
        _ = new BrowserWindow(c, shell, incognito: false, url: null);
        _ = new TextInfoWindow("Test", "ok");
        _ = new TextPromptWindow("Test", "Label", "");
        _ = new ThemeEditorWindow(new ThemeEditorViewModel(c.Settings));
        _ = new AboutWindow(c);
        AppLog.Info("smoke-test: all views built successfully");
        Environment.ExitCode = 0;
        Shutdown(0);
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private void OnUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled exception", e.Exception);
        MessageBox.Show("An unexpected error occurred. Details were written to the local log.",
                        "WinIDM", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
