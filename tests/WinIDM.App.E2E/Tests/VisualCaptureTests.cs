using System.Runtime.InteropServices;
using System.Windows.Automation;
using WinIDM.App.E2E.Support;

namespace WinIDM.App.E2E.Tests;

/// <summary>
/// Design QA capture. Not part of the functional suite: run it explicitly to save one screenshot per window
/// (<c>dotnet test --filter Category=Shots</c>), then review the PNG files in tests\WinIDM.App.E2E\artifacts.
/// </summary>
[Trait("Category", "Shots")]
public sealed class VisualCaptureTests
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static readonly (string Button, string Window, string Shot)[] Windows =
    {
        ("Settings", "Settings", "UI-settings"),
        ("Torrent", "Torrent", "UI-torrent"),
        ("Backup", "Backup and restore", "UI-backup"),
        ("Passwords", "Password manager", "UI-passwords"),
        ("Custom theme editor", "Custom theme editor", "UI-theme-editor"),
        ("About WinIDM", "About WinIDM", "UI-about"),
    };

    [Fact]
    public void Capture_EveryWindow()
    {
        var data = AppSession.NewTempDataDir();
        var failures = new List<string>();
        try
        {
            using var app = AppSession.Launch(data);
            Focus(app.Main);
            Shots.SaveWindow(app.Main, "UI-main");

            foreach (var (button, window, shot) in Windows)
            {
                try
                {
                    Uia.Invoke(Uia.Find(app.Main, ControlType.Button, button));
                    var element = Uia.WaitWindow(app.Pid, window, TimeSpan.FromSeconds(20));
                    Thread.Sleep(700);
                    Focus(element);
                    Shots.SaveWindow(element, shot);
                    if (element.TryGetCurrentPattern(WindowPattern.Pattern, out var wp)) ((WindowPattern)wp).Close();
                    Uia.WaitUntil(() => Uia.TryFindWindow(app.Pid, window) is null, $"window {window} closed", TimeSpan.FromSeconds(15));
                }
                catch (Exception ex)
                {
                    failures.Add($"{window}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // The Add URL dialog is modal and closes itself once cancelled.
            try
            {
                Uia.Invoke(Uia.Find(app.Main, ControlType.Button, "Add URL"));
                var dialog = Uia.WaitWindow(app.Pid, "Add download", TimeSpan.FromSeconds(20));
                Thread.Sleep(700);
                Focus(dialog);
                Shots.SaveWindow(dialog, "UI-add-download");
                AppFlows.CloseDialog(dialog);
                Uia.WaitWindowGone(app.Pid, "Add download", TimeSpan.FromSeconds(15));
            }
            catch (Exception ex)
            {
                failures.Add($"Add download: {ex.GetType().Name}: {ex.Message}");
            }

            // The scheduler tab uses its own panel instead of the download list.
            try
            {
                AppFlows.ClickTab(app, "Scheduler");
                Thread.Sleep(500);
                Focus(app.Main);
                Shots.SaveWindow(app.Main, "UI-scheduler");
                AppFlows.ClickTab(app, "All");
            }
            catch (Exception ex)
            {
                failures.Add($"Scheduler tab: {ex.GetType().Name}: {ex.Message}");
            }

            // The embedded browser needs a few seconds for WebView2 to render its start page.
            try
            {
                Uia.Invoke(Uia.Find(app.Main, ControlType.Button, "Browser"));
                var browser = Uia.WaitWindowEndingWith(app.Pid, "WinIDM Browser", TimeSpan.FromSeconds(40));
                Thread.Sleep(3000);
                Focus(browser);
                Shots.SaveWindow(browser, "UI-browser");
                if (browser.TryGetCurrentPattern(WindowPattern.Pattern, out var wp)) ((WindowPattern)wp).Close();
                Uia.WaitUntil(() => Uia.TryFindWindowEndingWith(app.Pid, "WinIDM Browser") is null,
                    "browser window closed", TimeSpan.FromSeconds(15));
            }
            catch (Exception ex)
            {
                failures.Add($"Browser: {ex.GetType().Name}: {ex.Message}");
            }

            Assert.Empty(failures);
        }
        finally
        {
            AppSession.TryDeleteDataDir(data);
        }
    }

    /// <summary>Bring a window to the foreground so the full-screen capture really shows it.</summary>
    private static void Focus(AutomationElement window)
    {
        try
        {
            var handle = window.Current.NativeWindowHandle;
            if (handle != 0) SetForegroundWindow(new IntPtr(handle));
            Thread.Sleep(500);
        }
        catch (ElementNotAvailableException)
        {
            // The window closed between the wait and the capture; the screenshot is still useful.
        }
    }
}


