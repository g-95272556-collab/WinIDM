using System.IO;
using System.Text;
using WinIDM.App.Services;
using WinIDM.Backup;
using WinIDM.Browser;
using WinIDM.Core.Models;
using WinIDM.Engine;

namespace WinIDM.App.Tests;

/// <summary>
/// Round-trip backup test (create -> restore) using the real concrete classes: JsonSettingsStore, SqliteDownloadRepository,
/// BookmarkStore, HistoryStore, AdblockConfigStore, SitePermissionStore and BackupService with the App BackupDataSource.
/// </summary>
public sealed class BackupRoundTripTests : IDisposable
{
    private const string BackupPassword = "test-password-12";
    private const string Marker = "UNIQUE-BOOKMARK-MARKER-7741";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "winidm-roundtrip-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = new();

    public BackupRoundTripTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch (Exception) { /* ignore during cleanup */ }
        }

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class TestEnv
    {
        public required string Dir { get; init; }
        public required JsonSettingsStore Raw { get; init; }
        public required SqliteDownloadRepository Repo { get; init; }
        public required BookmarkStore Bookmarks { get; init; }
        public required HistoryStore History { get; init; }
        public required AdblockConfigStore Adblock { get; init; }
        public required SitePermissionStore Sites { get; init; }
        public required AppBackupService Backup { get; init; }
    }

    private TestEnv Build(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);

        var raw = new JsonSettingsStore(Path.Combine(dir, "settings.json"));
        var repo = new SqliteDownloadRepository(Path.Combine(dir, "winidm.db"));
        _disposables.Add(repo);
        var vault = new PasswordVault(Path.Combine(dir, "vault.bin"));
        _disposables.Add(vault);
        var bookmarks = new BookmarkStore(Path.Combine(dir, "bookmarks.json"));
        var history = new HistoryStore(Path.Combine(dir, "history.json"));
        var adblock = new AdblockConfigStore(Path.Combine(dir, "adblock.json"));
        var sites = new SitePermissionStore(Path.Combine(dir, "site_permissions.json"));

        var source = new BackupDataSource(
            bookmarks, history, raw, vault, adblock,
            Path.Combine(dir, "userscripts.json"), sites, repo);
        var core = new BackupService(repo, new SecretFilteredSettings(raw), source, dir);

        return new TestEnv
        {
            Dir = dir,
            Raw = raw,
            Repo = repo,
            Bookmarks = bookmarks,
            History = history,
            Adblock = adblock,
            Sites = sites,
            Backup = new AppBackupService(core),
        };
    }

    [Fact]
    public async Task CreateThenRestore_RoundTripsDataAndKeepsSecretsOut()
    {
        var source = Build("source");
        source.Bookmarks.Add(Marker, "https://example.test/bookmark");
        source.History.Add("Test history", "https://example.test/history");
        source.Raw.Set("browser.saved_tabs", "[\"https://example.test/tab1\"]");
        source.Raw.Set("idm_pref_download_count", 5);
        source.Raw.Set("idm_pref_proxy_pass", "PROXY-SECRET-999");
        source.Adblock.Save(new AdblockConfig { Whitelist = new List<string> { "example.test" }, CustomRules = "||ads-test.test^" });
        source.Sites.Set("https://example.test", "notifications", true);

        var file = Path.Combine(_root, "backup.zip.enc");
        var categories = BackupCategories.Settings | BackupCategories.Bookmarks | BackupCategories.History
                         | BackupCategories.Adblock | BackupCategories.DomainLimits;
        await source.Backup.CreateAsync(file, categories, BackupPassword);

        Assert.True(File.Exists(file));
        // The backup is encrypted: the bookmark text does not appear in the file.
        Assert.DoesNotContain(Marker, Encoding.UTF8.GetString(File.ReadAllBytes(file)));

        var target = Build("target");
        target.Raw.Set("idm_pref_proxy_pass", "LOCAL-SECRET");

        var report = await target.Backup.RestoreWithReportAsync(file, categories, BackupPassword, RestoreMode.Overwrite);

        Assert.True(report.IsFullSuccess, report.ToText());
        Assert.Contains(target.Bookmarks.All, b => b.Title == Marker && b.Url == "https://example.test/bookmark");
        Assert.Contains(target.History.All, h => h.Title == "Test history");
        Assert.Equal("[\"https://example.test/tab1\"]", target.Raw.Get("browser.saved_tabs", ""));
        Assert.Equal(5, target.Raw.Get("idm_pref_download_count", 0));
        Assert.Contains("example.test", target.Adblock.Load().Whitelist);
        Assert.Contains("ads-test.test", target.Adblock.Load().CustomRules);
        Assert.True(target.Sites.Get("https://example.test", "notifications"));

        // Secrets are not exported and not overwritten: the local value is kept.
        Assert.Equal("LOCAL-SECRET", target.Raw.Get("idm_pref_proxy_pass", ""));
        Assert.DoesNotContain("PROXY-SECRET-999", string.Join(",", target.Raw.Snapshot().Values));
    }

    [Fact]
    public async Task Restore_WithWrongPassword_Fails()
    {
        var source = Build("source-wrong");
        source.Bookmarks.Add("Bookmark", "https://example.test/x");
        var file = Path.Combine(_root, "wrong.zip.enc");
        await source.Backup.CreateAsync(file, BackupCategories.Bookmarks, BackupPassword);

        var target = Build("target-wrong");
        await Assert.ThrowsAnyAsync<Exception>(() =>
            target.Backup.RestoreWithReportAsync(file, BackupCategories.Bookmarks, "other-password-12", RestoreMode.Merge));
        Assert.Empty(target.Bookmarks.All);
    }

    [Fact]
    public async Task PasswordCategory_RequiresBackupPasswordOfTwelveCharacters()
    {
        var env = Build("password");
        var file = Path.Combine(_root, "password.zip.enc");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            env.Backup.CreateAsync(file, BackupCategories.Passwords, "short"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            env.Backup.CreateAsync(file, BackupCategories.Passwords, null));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Restore_Merge_KeepsExistingBookmarks()
    {
        var source = Build("merge-source");
        source.Bookmarks.Add("From backup", "https://example.test/a");
        var file = Path.Combine(_root, "merge.zip.enc");
        await source.Backup.CreateAsync(file, BackupCategories.Bookmarks, BackupPassword);

        var target = Build("merge-target");
        target.Bookmarks.Add("Existing", "https://example.test/existing");
        var report = await target.Backup.RestoreWithReportAsync(file, BackupCategories.Bookmarks, BackupPassword, RestoreMode.Merge);

        Assert.True(report.IsFullSuccess);
        Assert.Contains(target.Bookmarks.All, b => b.Title == "Existing");
        Assert.Contains(target.Bookmarks.All, b => b.Title == "From backup");
    }
}
