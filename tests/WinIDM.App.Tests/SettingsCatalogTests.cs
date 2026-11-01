using System.IO;
using WinIDM.App.Services;
using Keys = WinIDM.App.Services.Keys;
using WinIDM.Engine;

namespace WinIDM.App.Tests;

public class SettingsCatalogTests
{
    [Fact]
    public void Defaults_MatchDeclaredKinds()
    {
        foreach (var def in SettingsCatalog.All)
        {
            switch (def.Kind)
            {
                case SettingKind.Bool: Assert.IsType<bool>(def.Default); break;
                case SettingKind.Int: Assert.IsType<int>(def.Default); break;
                case SettingKind.Text:
                case SettingKind.Choice: Assert.IsType<string>(def.Default); break;
            }
        }
    }

    [Fact]
    public void Choice_DefaultsAreAmongChoices()
    {
        foreach (var def in SettingsCatalog.All.Where(d => d.Kind == SettingKind.Choice))
        {
            Assert.NotNull(def.Choices);
            Assert.Contains((string)def.Default, def.Choices!);
        }
    }

    [Fact]
    public void AfterDownloadsAction_UsesEngineEnumNames()
    {
        Assert.True(SettingsCatalog.TryGet(Keys.AfterComplete, out var def));
        Assert.Equal(SettingKeys.AfterDownloadsAction, Keys.AfterComplete);
        foreach (var choice in def.Choices!)
        {
            Assert.True(Enum.TryParse<SystemAction>(choice, ignoreCase: false, out _), choice);
        }
    }

    [Fact]
    public void ThemeChoices_IncludeCustomTheme()
    {
        Assert.True(SettingsCatalog.TryGet(Keys.Theme, out var def));
        Assert.Contains("Custom", def.Choices!);
        Assert.Contains("Light", def.Choices!);
        Assert.Contains("Dark", def.Choices!);
        Assert.Contains("System", def.Choices!);
    }

    [Fact]
    public void BackupSecretKeys_AreStoredProtected()
    {
        foreach (var key in new[]
                 {
                     BackupKeys.GoogleSecret, BackupKeys.DropboxSecret, BackupKeys.RestToken, BackupKeys.SchedulePassword,
                 })
        {
            Assert.True(SettingKeys.IsSecret(key), key);
            Assert.True(SettingsCatalog.TryGet(key, out _), key);
        }
    }

    [Fact]
    public void DiagnosticsAndMeteredKeys_ExistWithSafeDefaults()
    {
        Assert.False(BoolDefault(Keys.CaptureLog));
        Assert.False(BoolDefault(Keys.DownloadDiagnostics));
        Assert.False(BoolDefault(Keys.PauseOnMetered));
        Assert.False(BoolDefault(Keys.WindowsHello));
        Assert.True(SettingsCatalog.TryGet(Keys.UpdateUrl, out var url));
        Assert.Equal("", url.Default);
    }

    /// <summary>
    /// One user-visible switch: the Settings row is keyed by the engine's metered-pause key, and the old App key is gone.
    /// </summary>
    [Fact]
    public void PauseOnMetered_IsTheEngineKey()
    {
        Assert.Equal(SettingKeys.PauseOnMetered, Keys.PauseOnMetered);
        Assert.True(SettingsCatalog.TryGet(Keys.PauseOnMetered, out var def));
        Assert.Equal(SettingKind.Bool, def.Kind);
        Assert.False((bool)def.Default);
        Assert.True(def.Visible);
        Assert.Equal("Pause downloads on a metered network", def.Label);
        Assert.False(SettingsCatalog.TryGet("app.pause_on_metered", out _));
    }

    /// <summary>
    /// The Settings toggle writes the key the engine reads (not an App-only key), and a settings file from an older
    /// build that still contains <c>app.pause_on_metered</c> loads without errors (the stale key is ignored).
    /// </summary>
    [Fact]
    public void PauseOnMetered_SettingsToggle_IsReadByTheEngineKey_AndStaleKeyIsTolerated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "winidm-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var raw = new JsonSettingsStore(Path.Combine(dir, "settings.json"));
            var settings = new AppSettings(raw);

            Assert.False(settings.Bool(Keys.PauseOnMetered));
            settings.Set(Keys.PauseOnMetered, true);
            Assert.True(raw.Get(SettingKeys.PauseOnMetered, false));

            var legacyPath = Path.Combine(dir, "legacy.json");
            File.WriteAllText(legacyPath, "{\"app.pause_on_metered\":\"true\"}");
            var legacy = new AppSettings(new JsonSettingsStore(legacyPath));
            Assert.False(legacy.Bool(Keys.PauseOnMetered));
            Assert.Equal("true", legacy.Snapshot()["app.pause_on_metered"]);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Keys_AreUniqueAcrossCatalog()
    {
        var keys = SettingsCatalog.All.Select(d => d.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    private static bool BoolDefault(string key)
    {
        Assert.True(SettingsCatalog.TryGet(key, out var def), key);
        return (bool)def.Default;
    }
}
