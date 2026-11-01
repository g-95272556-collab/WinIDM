using System.Text.Json;
using System.Windows.Automation;
using WinIDM.App.E2E.Support;

namespace WinIDM.App.E2E.Tests;

/// <summary>Scenario 5: settings open, one value is changed, saved, and survives an app restart.</summary>
[Trait("Category", "E2E")]
public sealed class SettingsTests
{
    private const string PartsKey = "idm_pref_download_part";
    private const string PartsLabel = "Number of parts per download (1-32)";

    [Fact]
    public void S05_Settings_ChangedValue_PersistsAfterRestart()
    {
        var data = AppSession.NewTempDataDir();
        try
        {
            var app = AppSession.Launch(data);
            try
            {
                Shots.RunScenario(app, "S05-settings-open-change", () =>
                {
                    Uia.Invoke(Uia.Find(app.Main, ControlType.Button, "Settings"));
                    var settings = Uia.WaitWindow(app.Pid, "Settings");
                    var edit = FindEditForLabel(settings, PartsLabel);
                    Assert.Equal("8", Uia.Value(edit));

                    // The page shows the real count; settings.json keeps the IDM+ value (count - 1) that the engine reads.
                    Uia.SetText(edit, "5");
                    Uia.WaitUntil(() => ReadSetting(data, PartsKey) == "4", "setting saved to settings.json", TimeSpan.FromSeconds(10));

                    AppFlows.CloseDialog(settings);
                    Uia.WaitWindowGone(app.Pid, "Settings");
                });
            }
            finally
            {
                app.Dispose();
            }

            // Restart the app with the same data folder: the value must persist.
            using var restarted = AppSession.Launch(data);
            Shots.RunScenario(restarted, "S05-settings-persist-after-restart", () =>
            {
                Assert.Equal("4", ReadSetting(data, PartsKey));

                Uia.Invoke(Uia.Find(restarted.Main, ControlType.Button, "Settings"));
                var settings = Uia.WaitWindow(restarted.Pid, "Settings");
                var edit = FindEditForLabel(settings, PartsLabel);
                Assert.Equal("5", Uia.Value(edit));
                AppFlows.CloseDialog(settings);
                Uia.WaitWindowGone(restarted.Pid, "Settings");
            });
        }
        finally
        {
            AppSession.TryDeleteDataDir(data);
        }
    }

    /// <summary>Find the TextBox (Edit) on the same row as the label: the closest ancestor that contains an Edit.</summary>
    private static AutomationElement FindEditForLabel(AutomationElement root, string label)
    {
        var labelEl = Uia.Find(root, ControlType.Text, label);
        var walker = TreeWalker.ControlViewWalker;
        for (var p = walker.GetParent(labelEl); p is not null; p = walker.GetParent(p))
        {
            var edit = Uia.TryFind(p, ControlType.Edit, null);
            if (edit is not null) return edit;
        }
        throw new InvalidOperationException($"Edit field for label \"{label}\" not found.");
    }

    private static string? ReadSetting(string dataDir, string key)
    {
        var path = Path.Combine(dataDir, "settings.json");
        if (!File.Exists(path)) return null;
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return dict is not null && dict.TryGetValue(key, out var v) ? v : null;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }
}
