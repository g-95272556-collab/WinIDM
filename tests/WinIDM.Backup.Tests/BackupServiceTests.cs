using System.IO.Compression;
using System.Text;
using WinIDM.Backup;
using WinIDM.Core.Models;

namespace WinIDM.Backup.Tests;

public class BackupServiceTests
{
    private const BackupCategories Everything = BackupScheduleOptions.DefaultCategories;
    private const BackupCategories WithPasswords = BackupScheduleOptions.DefaultCategories | BackupCategories.Passwords;

    private const string DownloadsManifest =
        "{\"format\":\"WinIDM.Backup\",\"schemaVersion\":1,\"createdUtc\":\"2026-10-31T00:00:00Z\",\"categories\":2,\"files\":[\"download_info_1.json\"]}";

    private static Harness Build(TempDir dir) => new(dir);

    private static async Task SeedAsync(Harness h)
    {
        h.Settings.Seed(("theme", "dark"), ("threads", "7"));

        var item = new DownloadItem
        {
            Url = "https://user:pw@example.com/a.bin",
            FileName = "a.bin",
            SaveDirectory = @"C:\Downloads",
            State = DownloadState.Completed,
            TotalBytes = 100,
            DownloadedBytes = 100,
            Password = "pw-secret",
            CookieHeader = "sid=secret",
            Headers = new Dictionary<string, string> { ["X-Test"] = "1", ["Authorization"] = "Bearer secret" },
        };
        var id = await h.Repo.InsertAsync(item);
        await h.Repo.SaveSegmentsAsync(id, new List<SegmentInfo>
        {
            new() { DownloadId = id, Index = 0, Start = 0, End = 49, Downloaded = 50, State = SegmentState.Done },
            new() { DownloadId = id, Index = 1, Start = 50, End = 99, Downloaded = 10, State = SegmentState.Active },
        });

        h.Extra.Data[BackupDataNames.Bookmarks] = "[{\"title\":\"WinIDM\",\"url\":\"https://winidm.example\"}]";
        h.Extra.Data[BackupDataNames.History] = "[{\"url\":\"https://a.example\"}]";
        h.Extra.Data[BackupDataNames.Tabs] = "[{\"url\":\"https://t.example\"}]";
        h.Extra.Data[BackupDataNames.Passwords] = "[{\"origin\":\"https://o.example\",\"user\":\"u\",\"password\":\"p\"}]";
        h.Extra.Data[BackupDataNames.Adblock] = "{\"whitelist\":[\"example.com\"]}";
        h.Extra.Data[BackupDataNames.UserScripts] = "[{\"name\":\"script-1\"}]";
        h.Extra.Data[BackupDataNames.SiteSettings] = "{\"example.com\":{\"desktop\":true}}";
    }

    [Fact]
    public async Task Plain_backup_round_trips_to_identical_state()
    {
        using var dir = new TempDir();
        using var source = Build(dir);
        await SeedAsync(source);
        var path = dir.File("backup.zip");

        await source.Service.CreateAsync(path, Everything, null);

        using (var zip = ZipFile.OpenRead(path))
        {
            var names = zip.Entries.Select(e => e.FullName).ToHashSet();
            Assert.Contains("manifest.json", names);
            Assert.Contains("idm_settings.json", names);
            Assert.Contains("download_info_1.json", names);
            Assert.Contains("bookmarks.json", names);
            Assert.Contains("history.json", names);
            Assert.Contains("tabs.json", names);
            Assert.Contains("adblock.json", names);
            Assert.Contains("userscripts.json", names);
            Assert.Contains("sitesettings.json", names);
            Assert.DoesNotContain("passwords.json", names);
        }

        using var target = Build(dir);
        var report = await target.Service.RestoreWithReportAsync(path, Everything, null, RestoreMode.Merge);

        Assert.True(report.IsFullSuccess, report.ToText());
        Assert.Equal("dark", target.Settings.Get("theme", string.Empty));
        Assert.Equal("7", target.Settings.Get("threads", string.Empty));

        var restored = Assert.Single(target.Repo.All);
        Assert.Equal("https://example.com/a.bin", restored.Url);
        Assert.Equal(DownloadState.Completed, restored.State);
        Assert.Null(restored.Password);
        Assert.Null(restored.CookieHeader);
        Assert.Equal("1", restored.Headers["X-Test"]);
        Assert.False(restored.Headers.ContainsKey("Authorization"));

        var segments = target.Repo.SegmentsOf(restored.Id);
        Assert.Equal(2, segments.Count);
        Assert.Equal(SegmentState.Done, segments[0].State);
        Assert.Equal(SegmentState.Pending, segments[1].State);
        Assert.Equal(restored.Id, segments[1].DownloadId);

        foreach (var name in new[] { "bookmarks", "history", "tabs", "adblock", "userscripts", "sitesettings" })
        {
            Assert.Equal(source.Extra.Data[name], target.Extra.Data[name]);
        }
    }

    [Fact]
    public async Task Encrypted_backup_with_passwords_needs_correct_password()
    {
        using var dir = new TempDir();
        using var source = Build(dir);
        await SeedAsync(source);
        var path = dir.File("backup-enc.zip");

        await source.Service.CreateAsync(path, WithPasswords, "Secret-123");

        var bytes = File.ReadAllBytes(path);
        Assert.Equal("1DMCRYPT", Encoding.ASCII.GetString(bytes, 0, 8));

        using var target = Build(dir);
        await Assert.ThrowsAsync<BackupPasswordRequiredException>(() => target.Service.RestoreAsync(path, WithPasswords, null));
        await Assert.ThrowsAsync<BackupAuthenticationException>(() => target.Service.RestoreAsync(path, WithPasswords, "wrong"));

        var report = await target.Service.RestoreWithReportAsync(path, WithPasswords, "Secret-123", RestoreMode.Merge);

        Assert.True(report.IsFullSuccess, report.ToText());
        Assert.Equal(source.Extra.Data[BackupDataNames.Passwords], target.Extra.Data[BackupDataNames.Passwords]);
    }

    [Fact]
    public async Task Passwords_category_without_password_is_refused()
    {
        using var dir = new TempDir();
        using var source = Build(dir);
        await SeedAsync(source);

        await Assert.ThrowsAsync<BackupException>(() =>
            source.Service.CreateAsync(dir.File("x.zip"), BackupCategories.Passwords, null));
        await Assert.ThrowsAsync<BackupException>(() =>
            source.Service.CreateAsync(dir.File("y.zip"), BackupCategories.Passwords, string.Empty));
    }

    [Fact]
    public async Task Corrupted_header_is_rejected()
    {
        using var dir = new TempDir();
        using var source = Build(dir);
        await SeedAsync(source);
        var path = dir.File("corrupt.zip");
        await source.Service.CreateAsync(path, Everything, "password");

        var bytes = File.ReadAllBytes(path);
        bytes[0] = (byte)'X';
        File.WriteAllBytes(path, bytes);

        await Assert.ThrowsAsync<BackupFormatException>(() => source.Service.RestoreAsync(path, Everything, "password"));
    }

    [Fact]
    public async Task Truncated_encrypted_file_is_rejected()
    {
        using var dir = new TempDir();
        using var source = Build(dir);
        await SeedAsync(source);
        var path = dir.File("truncated.zip");
        await source.Service.CreateAsync(path, Everything, "password");

        File.WriteAllBytes(path, File.ReadAllBytes(path).AsSpan(0, 20).ToArray());

        await Assert.ThrowsAsync<BackupFormatException>(() => source.Service.RestoreAsync(path, Everything, "password"));
    }

    [Fact]
    public async Task Tampered_encrypted_payload_is_rejected()
    {
        using var dir = new TempDir();
        using var source = Build(dir);
        await SeedAsync(source);
        var path = dir.File("tampered.zip");
        await source.Service.CreateAsync(path, Everything, "password");

        var bytes = File.ReadAllBytes(path);
        bytes[BackupCrypto.HeaderSize + 5] ^= 0x10;
        File.WriteAllBytes(path, bytes);

        await Assert.ThrowsAsync<BackupAuthenticationException>(() => source.Service.RestoreAsync(path, Everything, "password"));
    }

    [Fact]
    public async Task Zip_slip_entry_rejects_whole_backup_before_any_change()
    {
        using var dir = new TempDir();
        using var target = Build(dir);
        target.Settings.Seed(("theme", "original"));
        var path = dir.File("zip-slip.zip");
        TestHttp.WriteZip(path,
            ("manifest.json", TestHttp.ManifestSettingsOnly),
            ("idm_settings.json", "{\"theme\":\"attacked\"}"),
            ("../../evil.json", "{}"));

        await Assert.ThrowsAsync<BackupSecurityException>(() => target.Service.RestoreAsync(path, Everything, null));

        Assert.Equal("original", target.Settings.Get("theme", string.Empty));
    }

    [Fact]
    public async Task Backup_without_manifest_is_rejected()
    {
        using var dir = new TempDir();
        using var target = Build(dir);
        var path = dir.File("no-manifest.zip");
        TestHttp.WriteZip(path, ("idm_settings.json", "{\"theme\":\"dark\"}"));

        await Assert.ThrowsAsync<BackupFormatException>(() => target.Service.RestoreAsync(path, Everything, null));
    }

    [Fact]
    public async Task Manifest_listing_missing_file_is_rejected()
    {
        using var dir = new TempDir();
        using var target = Build(dir);
        var path = dir.File("missing.zip");
        TestHttp.WriteZip(path, ("manifest.json", TestHttp.ManifestSettingsOnly));

        await Assert.ThrowsAsync<BackupFormatException>(() => target.Service.RestoreAsync(path, Everything, null));
    }

    [Fact]
    public async Task Partial_restore_reports_failed_unit_and_keeps_the_rest()
    {
        using var dir = new TempDir();
        using var source = Build(dir);
        await SeedAsync(source);
        var path = dir.File("partial.zip");
        var categories = BackupCategories.Settings | BackupCategories.Bookmarks | BackupCategories.History;
        await source.Service.CreateAsync(path, categories, null);

        using var target = Build(dir);
        target.Extra.FailOnImport.Add(BackupDataNames.Bookmarks);

        var ex = await Assert.ThrowsAsync<BackupPartialRestoreException>(() => target.Service.RestoreAsync(path, categories, null));

        Assert.Contains(ex.Report.Outcomes, o => o.Item == "bookmarks.json" && o.Status == RestoreStatus.Failed);
        Assert.Contains(ex.Report.Outcomes, o => o.Item == "history.json" && o.Status == RestoreStatus.Restored);
        Assert.Contains(ex.Report.Outcomes, o => o.Item == "idm_settings.json" && o.Status == RestoreStatus.Restored);
        Assert.Equal("dark", target.Settings.Get("theme", string.Empty));
        Assert.Equal(source.Extra.Data[BackupDataNames.History], target.Extra.Data[BackupDataNames.History]);
        Assert.Contains("FAILED", ex.Report.ToText());
    }

    [Fact]
    public async Task Restore_only_touches_selected_categories()
    {
        using var dir = new TempDir();
        using var source = Build(dir);
        await SeedAsync(source);
        var path = dir.File("selected.zip");
        await source.Service.CreateAsync(path, Everything, null);

        using var target = Build(dir);
        var report = await target.Service.RestoreWithReportAsync(path, BackupCategories.Settings, null, RestoreMode.Merge);

        Assert.True(report.IsFullSuccess);
        Assert.Empty(target.Extra.Imports);
        Assert.Empty(target.Repo.All);
        Assert.Equal("dark", target.Settings.Get("theme", string.Empty));
    }

    [Fact]
    public async Task Merge_keeps_local_settings_while_overwrite_replaces_them()
    {
        using var dir = new TempDir();
        using var source = Build(dir);
        source.Settings.Seed(("b", "9"));
        var path = dir.File("settings.zip");
        await source.Service.CreateAsync(path, BackupCategories.Settings, null);

        using var merged = Build(dir);
        merged.Settings.Seed(("a", "1"), ("b", "2"));
        await merged.Service.RestoreAsync(path, BackupCategories.Settings, null);
        Assert.Equal("1", merged.Settings.Get("a", "missing"));
        Assert.Equal("9", merged.Settings.Get("b", "missing"));

        using var overwritten = Build(dir);
        overwritten.Settings.Seed(("a", "1"), ("b", "2"));
        var report = await overwritten.Service.RestoreWithReportAsync(path, BackupCategories.Settings, null, RestoreMode.Overwrite);
        Assert.True(report.IsFullSuccess);
        Assert.Equal("missing", overwritten.Settings.Get("a", "missing"));
        Assert.Equal("9", overwritten.Settings.Get("b", "missing"));
    }

    [Fact]
    public async Task Merge_skips_existing_downloads_and_overwrite_replaces_them()
    {
        using var dir = new TempDir();
        using var source = Build(dir);
        var x = await source.Repo.InsertAsync(new DownloadItem { Url = "https://ex/x", FileName = "x.bin", SaveDirectory = @"C:\D" });
        await source.Repo.InsertAsync(new DownloadItem { Url = "https://ex/y", FileName = "y.bin", SaveDirectory = @"C:\D" });
        Assert.True(x > 0);
        var path = dir.File("downloads.zip");
        await source.Service.CreateAsync(path, BackupCategories.Downloads, null);

        using var merged = Build(dir);
        await merged.Repo.InsertAsync(new DownloadItem { Url = "https://ex/x", FileName = "x.bin", SaveDirectory = @"C:\D" });
        await merged.Repo.InsertAsync(new DownloadItem { Url = "https://other", FileName = "o.bin", SaveDirectory = @"C:\D" });
        await merged.Service.RestoreAsync(path, BackupCategories.Downloads, null);
        Assert.Equal(3, merged.Repo.All.Count);

        using var overwritten = Build(dir);
        await overwritten.Repo.InsertAsync(new DownloadItem { Url = "https://other", FileName = "o.bin", SaveDirectory = @"C:\D" });
        await overwritten.Service.RestoreWithReportAsync(path, BackupCategories.Downloads, null, RestoreMode.Overwrite);
        Assert.Equal(new[] { "https://ex/x", "https://ex/y" }, overwritten.Repo.All.Select(i => i.Url).OrderBy(u => u).ToArray());
    }

    [Fact]
    public async Task Invalid_settings_schema_fails_only_that_unit()
    {
        using var dir = new TempDir();
        using var target = Build(dir);
        target.Settings.Seed(("theme", "persisted"));
        var path = dir.File("bad-schema.zip");
        TestHttp.WriteZip(path,
            ("manifest.json", TestHttp.ManifestSettingsOnly),
            ("idm_settings.json", "{\"theme\": 5}"));

        var report = await target.Service.RestoreWithReportAsync(path, BackupCategories.Settings, null, RestoreMode.Merge);

        var outcome = Assert.Single(report.Outcomes);
        Assert.Equal(RestoreStatus.Failed, outcome.Status);
        Assert.False(report.IsFullSuccess);
        Assert.Equal("persisted", target.Settings.Get("theme", string.Empty));
    }

    [Fact]
    public async Task Download_record_with_unsafe_file_name_is_rejected()
    {
        using var dir = new TempDir();
        using var target = Build(dir);
        var path = dir.File("invalid-name.zip");
        TestHttp.WriteZip(path,
            ("manifest.json", DownloadsManifest),
            ("download_info_1.json", "{\"schemaVersion\":1,\"item\":{\"url\":\"https://a.example/x\",\"fileName\":\"../x.bin\",\"saveDirectory\":\"C:\\\\D\"},\"segments\":[]}"));

        var report = await target.Service.RestoreWithReportAsync(path, BackupCategories.Downloads, null, RestoreMode.Merge);

        Assert.Contains(report.Outcomes, o => o.Category == BackupCategories.Downloads && o.Status == RestoreStatus.Failed);
        Assert.Empty(target.Repo.All);
    }

    [Fact]
    public async Task Source_export_failure_aborts_creation_without_writing_destination()
    {
        using var dir = new TempDir();
        using var source = Build(dir);
        await SeedAsync(source);
        source.Extra.Data[BackupDataNames.Bookmarks] = "not json {";
        var path = dir.File("failed.zip");

        await Assert.ThrowsAsync<BackupFormatException>(() => source.Service.CreateAsync(path, BackupCategories.Bookmarks, null));
        Assert.False(File.Exists(path));
    }

    /// <summary>Test context: repository, settings, data source and service in one directory.</summary>
    private sealed class Harness : IDisposable
    {
        public Harness(TempDir dir)
        {
            Repo = new FakeDownloadRepository();
            Settings = new FakeSettingsStore();
            Extra = new FakeDataSource();
            Service = new BackupService(Repo, Settings, Extra, Path.Combine(dir.Path, "data"));
        }

        public FakeDownloadRepository Repo { get; }

        public FakeSettingsStore Settings { get; }

        public FakeDataSource Extra { get; }

        public BackupService Service { get; }

        public void Dispose()
        {
        }
    }
}
