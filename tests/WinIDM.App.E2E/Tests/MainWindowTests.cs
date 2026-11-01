using System.Text.RegularExpressions;
using System.Windows.Automation;
using WinIDM.App.E2E.Support;

namespace WinIDM.App.E2E.Tests;

/// <summary>Scenario 1: startup, the main window, 11 filter tabs and the toolbar.</summary>
[Trait("Category", "E2E")]
public sealed class MainWindowTests
{
    private static readonly string[] Toolbar =
    {
        "Add URL", "Start all", "Pause all", "Settings", "Browser", "Torrent", "Backup", "Passwords",
    };

    [Fact]
    public void S01_Startup_ShowsMainWindow_ElevenTabs_AndToolbar()
    {
        var data = AppSession.NewTempDataDir();
        try
        {
            using var app = AppSession.Launch(data);
            Shots.RunScenario(app, "S01-startup", () =>
            {
                var main = app.Main;
                Assert.Equal(AppSession.MainTitle, Uia.SafeName(main));

                var tabPattern = new Regex(@"^(" + string.Join("|", AppFlows.TabTitles.Select(Regex.Escape)) + @") \(\d+\)$");
                var tabNames = Uia.FindAll(main, ControlType.Button)
                    .Select(Uia.SafeName)
                    .Where(n => tabPattern.IsMatch(n))
                    .ToList();
                Assert.Equal(11, tabNames.Count);
                foreach (var title in AppFlows.TabTitles)
                    Assert.Contains(tabNames, n => n.StartsWith(title + " (", StringComparison.Ordinal));

                foreach (var label in Toolbar)
                    Assert.NotNull(Uia.TryFind(main, ControlType.Button, label));

                // The initial count for every tab is 0 on an empty profile.
                Assert.Equal(0, Uia.TabCount(main, "All"));
            });
        }
        finally
        {
            AppSession.TryDeleteDataDir(data);
        }
    }
}
