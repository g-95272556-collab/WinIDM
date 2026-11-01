using System.Diagnostics;
using System.IO;
using System.Windows;
using WinIDM.Engine;

namespace WinIDM.App.Services;

/// <summary>
/// Wires engine events into the application: completion/error notifications (tray toast) and system
/// actions after all downloads finish. System actions ALWAYS require user confirmation first. The engine only
/// reports events; file moves are performed by the engine according to settings (<c>idm_pref_move_files_download_finish</c>).
/// </summary>
public sealed class PostDownloadCoordinator : IDisposable
{
    private readonly Composition _c;
    private readonly IShell _shell;
    private readonly Action<string, string, bool> _notify;
    private readonly Action _exitApp;
    private readonly Action<string, string> _announce;
    private bool _attached;

    public PostDownloadCoordinator(
        Composition composition,
        IShell shell,
        Action<string, string, bool> notify,
        Action exitApp)
    {
        _c = composition;
        _shell = shell;
        _notify = notify;
        _exitApp = exitApp;
        _announce = (title, text) => _notify(title, text, false);
    }

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _c.EngineImpl.DownloadCompleted += OnCompleted;
        _c.EngineImpl.DownloadFailed += OnFailed;
        _c.EngineImpl.AfterDownloadsFinished += OnAfterDownloadsFinished;
    }

    public void Dispose()
    {
        if (!_attached) return;
        _attached = false;
        _c.EngineImpl.DownloadCompleted -= OnCompleted;
        _c.EngineImpl.DownloadFailed -= OnFailed;
        _c.EngineImpl.AfterDownloadsFinished -= OnAfterDownloadsFinished;
    }

    private void OnCompleted(DownloadCompletedInfo info)
    {
        // Diagnostics: id and size only (no file name or URL).
        AppLog.Info($"Download #{info.Id} finished; {info.TotalBytes} bytes; category {info.Category}.");
        if (!_c.Settings.Bool(Keys.ToastComplete) || _c.Settings.Bool(Keys.DisableAllToasts)) return;

        var text = info.FileName;
        if (!string.IsNullOrWhiteSpace(info.MovedTo?.ToString()))
            text += $" moved to {Path.GetDirectoryName(info.MovedTo.ToString()!) ?? info.MovedTo.ToString()}";
        Dispatch(() => _announce("Download finished", text));
    }

    private void OnFailed(DownloadFailedInfo info)
    {
        AppLog.Info($"Download #{info.Id} error {info.ErrorCode}.");
        if (!_c.Settings.Bool(Keys.ToastError) || _c.Settings.Bool(Keys.DisableAllToasts)) return;
        Dispatch(() => _notify("Download error", info.FileName, true));
    }

    private void OnAfterDownloadsFinished(SystemActionRequest request)
    {
        if (request.CompletedCount > 0) Dispatch(() => SoundService.Play(_c.Settings, Keys.SoundCompleteAll));
        if (request.Action == SystemAction.None) return;
        Dispatch(() => RunWithConfirmation(request));
    }

    private void RunWithConfirmation(SystemActionRequest request)
    {
        var (title, label) = Describe(request.Action);
        var message = $"All downloads finished ({request.CompletedCount} completed, {request.FailedCount} failed).\n\n" +
                      $"Perform action: {label}?\n" +
                      "For shutdown or restart a 60 second delay is given; use 'shutdown /a' to cancel.";
        if (!_shell.Confirm(title, message))
        {
            _announce("WinIDM", "The after-completion action was canceled by the user.");
            return;
        }

        try
        {
            switch (request.Action)
            {
                case SystemAction.Shutdown:
                    Start("shutdown.exe", "/s /t 60 /c \"WinIDM: all downloads finished\"");
                    break;
                case SystemAction.Restart:
                    Start("shutdown.exe", "/r /t 60 /c \"WinIDM: all downloads finished\"");
                    break;
                case SystemAction.Hibernate:
                    Start("shutdown.exe", "/h");
                    break;
                case SystemAction.Sleep:
                    Start("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
                    break;
                case SystemAction.LockWorkstation:
                    Start("rundll32.exe", "user32.dll,LockWorkStation");
                    break;
                case SystemAction.CloseApplication:
                    _exitApp();
                    break;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error("After-completion action", ex);
            _announce("WinIDM", "The action could not be performed. See the log for details.");
        }
    }

    private static (string Title, string Label) Describe(SystemAction action) => action switch
    {
        SystemAction.Shutdown => ("Shut down", "shut down the computer (60 seconds)"),
        SystemAction.Restart => ("Restart", "restart the computer (60 seconds)"),
        SystemAction.Sleep => ("Sleep", "put the computer to sleep"),
        SystemAction.Hibernate => ("Hibernate", "hibernate the computer"),
        SystemAction.LockWorkstation => ("Lock screen", "lock the Windows screen"),
        SystemAction.CloseApplication => ("Exit application", "close WinIDM"),
        _ => ("WinIDM", "none"),
    };

    private static void Start(string file, string args) =>
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true });

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(action);
    }
}
