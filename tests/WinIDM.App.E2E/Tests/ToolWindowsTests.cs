using System.Windows.Automation;
using WinIDM.App.E2E.Support;

namespace WinIDM.App.E2E.Tests;

/// <summary>
/// Scenario 7: the Torrent window opens without errors; Backup opens and creates a backup in a temporary folder
/// (outside the user's data folder); the theme editor opens from the main window navigation rail.
/// </summary>
[Trait("Category", "E2E")]
public sealed class ToolWindowsTests
{
    [Fact]
    public void S07_TorrentWindow_OpensWithoutError()
    {
        var data = AppSession.NewTempDataDir();
        try
        {
            using var app = AppSession.Launch(data);
            Shots.RunScenario(app, "S07a-torrent-window", () =>
            {
                AppFlows.ClickButton(app, "Torrent");
                var torrent = Uia.WaitWindow(app.Pid, "Torrent", TimeSpan.FromSeconds(20));

                // No error box (#32770) within a short period after the window opens.
                Thread.Sleep(2000);
                var errorBox = Uia.WindowsOfProcess(app.Pid).FirstOrDefault(w => w.Current.ClassName == "#32770");
                Assert.True(errorBox is null, $"A message box appeared after Torrent opened: \"{(errorBox is null ? "" : Uia.SafeName(errorBox))}\".");
                Assert.NotNull(Uia.TryFind(torrent, ControlType.Button, null));

                CloseWindow(torrent);
                Uia.WaitUntil(() => Uia.TryFindWindow(app.Pid, "Torrent") is null, "Torrent window closed", TimeSpan.FromSeconds(15));
                Assert.NotNull(app.Main);
            });
        }
        finally
        {
            AppSession.TryDeleteDataDir(data);
        }
    }

    [Fact]
    public void S07_BackupWindow_CreatesLocalBackupInTempFolder()
    {
        var data = AppSession.NewTempDataDir();
        var backupDir = Path.Combine(Path.GetTempPath(), "winidm-e2e-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupDir);
        try
        {
            var target = Path.Combine(backupDir, "backup-e2e.zip");
            using var app = AppSession.Launch(data);
            Shots.RunScenario(app, "S07b-backup-local", () =>
            {
                AppFlows.ClickButton(app, "Backup");
                var backup = Uia.WaitWindow(app.Pid, "Backup and restore", TimeSpan.FromSeconds(20));

                // Target: the text field next to the "Save to..." button.
                Uia.SetText(Uia.FindEditNear(Uia.Find(backup, ControlType.Button, "Save to...")), target);
                Uia.Invoke(Uia.Find(backup, ControlType.Button, "Create backup"));

                try
                {
                    Uia.WaitUntil(() => File.Exists(target) && new FileInfo(target).Length > 0, "backup file created", TimeSpan.FromSeconds(60));
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException("The backup file was not created. Backup window contents: " +
                                               AppFlows.AllText(backup).Replace("\n", " | "));
                }
                Assert.True(new FileInfo(target).Length > 0, "The backup file is empty.");
                Assert.Contains(target, AppFlows.AllText(backup));

                CloseWindow(backup);
                Uia.WaitUntil(() => Uia.TryFindWindow(app.Pid, "Backup and restore") is null, "backup window closed", TimeSpan.FromSeconds(15));
            });
        }
        finally
        {
            AppSession.TryDeleteDataDir(data);
            AppSession.TryDeleteDataDir(backupDir);
        }
    }

    [Fact]
    public void S07_ThemeEditor_OpensFromMainWindow()
    {
        var data = AppSession.NewTempDataDir();
        try
        {
            using var app = AppSession.Launch(data);
            Shots.RunScenario(app, "S07c-theme-editor", () =>
            {
                // The theme editor is reachable from the main window navigation rail ("Custom theme").
                Uia.Invoke(Uia.Find(app.Main, ControlType.Button, "Custom theme editor"));
                var editor = Uia.WaitWindow(app.Pid, "Custom theme editor", TimeSpan.FromSeconds(20));
                Assert.NotNull(editor);
                CloseWindow(editor);
                Uia.WaitUntil(() => Uia.TryFindWindow(app.Pid, "Custom theme editor") is null, "theme editor closed", TimeSpan.FromSeconds(15));
                Assert.NotNull(app.Main);
            });
        }
        finally
        {
            AppSession.TryDeleteDataDir(data);
        }
    }

    private static void CloseWindow(AutomationElement window)
    {
        if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var wp)) ((WindowPattern)wp).Close();
        else throw new InvalidOperationException("The window does not support WindowPattern.Close.");
    }
}
