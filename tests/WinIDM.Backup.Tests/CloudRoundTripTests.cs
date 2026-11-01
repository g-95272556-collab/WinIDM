using WinIDM.Backup;
using WinIDM.Core.Models;

namespace WinIDM.Backup.Tests;

/// <summary>Full round trip: create -> upload to the mock server -> list -> download -> restore -> identical data.</summary>
public class CloudRoundTripTests
{
    private const string Password = "test-password-12";
    private static readonly BackupCategories Categories = BackupScheduleOptions.DefaultCategories | BackupCategories.Passwords;

    private sealed class Node
    {
        public Node(string dir)
        {
            Repo = new FakeDownloadRepository();
            Settings = new FakeSettingsStore();
            Extra = new FakeDataSource();
            Service = new BackupService(Repo, Settings, Extra, dir);
        }

        public FakeDownloadRepository Repo { get; }

        public FakeSettingsStore Settings { get; }

        public FakeDataSource Extra { get; }

        public BackupService Service { get; }
    }

    /// <summary>Source node with mixed data. <paramref name="padding"/> adds random bytes so the file is large enough.</summary>
    private static Node Seeded(string dir, int padding = 0)
    {
        var node = new Node(dir);
        node.Settings.Seed(("theme", "dark"), ("threads", "7"));
        node.Repo.InsertAsync(new DownloadItem
        {
            Url = "https://sample.example/file-a.bin",
            FileName = "file-a.bin",
            SaveDirectory = @"C:\Downloads",
            State = DownloadState.Completed,
            TotalBytes = 100,
            DownloadedBytes = 100,
        }).GetAwaiter().GetResult();
        node.Repo.InsertAsync(new DownloadItem
        {
            Url = "https://sample.example/file-b.bin",
            FileName = "file-b.bin",
            SaveDirectory = @"C:\Downloads",
            State = DownloadState.Completed,
            TotalBytes = 200,
            DownloadedBytes = 200,
        }).GetAwaiter().GetResult();

        var payload = padding == 0 ? string.Empty : Convert.ToBase64String(CloudTest.RandomBytes(padding, seed: 21));
        node.Extra.Data["bookmarks"] = $"[{{\"title\":\"Sample\",\"url\":\"https://sample.example\",\"note\":\"{payload}\"}}]";
        node.Extra.Data["passwords"] = "[{\"site\":\"sample.example\"}]";
        return node;
    }

    private static Node Empty(string dir) => new(dir);

    private static List<(string Key, string Value)> Ordered(IReadOnlyDictionary<string, string> values) =>
        values.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)).ToList();

    private static void AssertSameData(Node source, Node target)
    {
        Assert.Equal(Ordered(source.Settings.Snapshot()), Ordered(target.Settings.Snapshot()));
        Assert.Equal(source.Repo.All.Select(i => i.Url), target.Repo.All.Select(i => i.Url));
        Assert.Equal(source.Extra.Data["bookmarks"], target.Extra.Data["bookmarks"]);
        Assert.Equal(source.Extra.Data["passwords"], target.Extra.Data["passwords"]);
    }

    [Fact]
    public async Task Full_cycle_on_google_drive_restores_identical_data()
    {
        using var dir = new TempDir();
        using var server = new FakeDriveServer();
        using var http = CloudTest.NewHttp();
        var source = Seeded(dir.File("data-a"));
        var staging = dir.File("backup.zip.enc");
        await source.Service.CreateAsync(staging, Categories, Password);

        var drive = new GoogleDriveDestination(new FixedToken(), http, new GoogleDriveOptions
        {
            ApiBase = new Uri(server.BaseUri, "drive/v3/"),
            UploadBase = new Uri(server.BaseUri, "upload/drive/v3/"),
        });
        var name = BackupNames.NewName(CloudTest.Start, encrypted: true);
        await drive.UploadAsync(staging, name);

        var listed = Assert.Single(await drive.ListAsync());
        Assert.Equal(name, listed.Name);

        var downloaded = dir.File("download.zip.enc");
        await drive.DownloadAsync(name, downloaded);
        Assert.Equal(File.ReadAllBytes(staging), File.ReadAllBytes(downloaded));

        var target = Empty(dir.File("data-b"));
        var report = await target.Service.RestoreWithReportAsync(downloaded, Categories, Password, RestoreMode.Overwrite);

        Assert.True(report.IsFullSuccess, report.ToText());
        AssertSameData(source, target);

        await drive.DeleteAsync(name);
        Assert.Empty(await drive.ListAsync());
    }

    [Fact]
    public async Task Full_cycle_on_dropbox_with_session_upload_and_lost_response_restores_identical_data()
    {
        using var dir = new TempDir();
        using var server = new FakeDropboxServer();
        using var http = CloudTest.NewHttp();
        var source = Seeded(dir.File("data-a"), padding: 200 * 1024);
        var staging = dir.File("backup-large.zip.enc");
        await source.Service.CreateAsync(staging, Categories, Password);
        Assert.True(new FileInfo(staging).Length > 128 * 1024, "The backup file must exceed the single-upload limit.");

        var chunk = 64 * 1024;
        server.AddFault(new FakeFault(r =>
                r.Path == "/2/files/upload_session/append_v2"
                && r.Header("Dropbox-API-Arg") is not null
                && CloudTest.DropboxArgOffset(r) == 2L * chunk,
            FaultKind.AbortAfterApply));

        var dropbox = new DropboxDestination(new FixedToken(), http, new DropboxOptions
        {
            ApiBase = new Uri(server.BaseUri, "2/"),
            ContentBase = new Uri(server.BaseUri, "2/"),
            SingleUploadMaxBytes = 64 * 1024,
            SessionChunkBytes = chunk,
        });
        var name = BackupNames.NewName(CloudTest.Start, encrypted: true);
        await dropbox.UploadAsync(staging, name);

        var listed = Assert.Single(await dropbox.ListAsync());
        Assert.Equal(name, listed.Name);
        Assert.Equal(File.ReadAllBytes(staging), server.ContentAt("/WinIDM Backups/" + name));

        var downloaded = dir.File("download.zip.enc");
        await dropbox.DownloadAsync(name, downloaded);

        var target = Empty(dir.File("data-b"));
        var report = await target.Service.RestoreWithReportAsync(downloaded, Categories, Password, RestoreMode.Overwrite);

        Assert.True(report.IsFullSuccess, report.ToText());
        AssertSameData(source, target);
    }

    [Fact]
    public async Task Overwrite_restore_replaces_existing_settings_while_merge_keeps_them()
    {
        using var dir = new TempDir();
        var source = Seeded(dir.File("data-a"));
        var staging = dir.File("backup.zip");
        await source.Service.CreateAsync(staging, BackupScheduleOptions.DefaultCategories, password: null);

        var merged = Empty(dir.File("merge"));
        merged.Settings.Seed(("existing", "yes"));
        await merged.Service.RestoreWithReportAsync(staging, BackupScheduleOptions.DefaultCategories, null, RestoreMode.Merge);
        Assert.True(merged.Settings.Snapshot().ContainsKey("existing"));

        var overwritten = Empty(dir.File("overwrite"));
        overwritten.Settings.Seed(("existing", "yes"));
        await overwritten.Service.RestoreWithReportAsync(staging, BackupScheduleOptions.DefaultCategories, null, RestoreMode.Overwrite);
        Assert.False(overwritten.Settings.Snapshot().ContainsKey("existing"));
        Assert.Equal("dark", overwritten.Settings.Snapshot()["theme"]);
    }

    [Fact]
    public async Task Scheduler_uploads_to_selected_destination_and_applies_retention()
    {
        using var dir = new TempDir();
        using var server = new FakeDriveServer();
        using var http = CloudTest.NewHttp();
        var source = Seeded(dir.File("data-a"));
        var clock = new FakeClock(CloudTest.Start);
        var drive = new GoogleDriveDestination(new FixedToken(), http, new GoogleDriveOptions
        {
            ApiBase = new Uri(server.BaseUri, "drive/v3/"),
            UploadBase = new Uri(server.BaseUri, "upload/drive/v3/"),
        });
        var options = new BackupScheduleOptions
        {
            Enabled = true,
            IntervalHours = 1,
            Password = Password,
            Categories = BackupScheduleOptions.DefaultCategories,
            RetentionCount = 1,
        };
        var scheduler = new BackupScheduler(source.Service, drive, options, clock, dir.File("staging"));

        var first = await scheduler.RunNowAsync();
        clock.Advance(TimeSpan.FromMinutes(5));
        var second = await scheduler.RunNowAsync();

        Assert.True(first.Success);
        Assert.True(second.Success, second.Error);
        Assert.Equal(1, second.DeletedByRetention);
        var only = Assert.Single(await drive.ListAsync());
        Assert.Equal(second.RemoteName, only.Name);
    }
}
