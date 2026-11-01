using System.Net;
using WinIDM.Backup;

namespace WinIDM.Backup.Tests;

public class GoogleDriveDestinationTests
{
    private const int Chunk = 256 * 1024;

    private static GoogleDriveOptions Options(
        FakeDriveServer server,
        GoogleDriveLocation location = GoogleDriveLocation.Root,
        long multipartMax = 5L * 1024 * 1024,
        int pageSize = 1000,
        CloudOptions? cloud = null) => new()
    {
        ApiBase = new Uri(server.BaseUri, "drive/v3/"),
        UploadBase = new Uri(server.BaseUri, "upload/drive/v3/"),
        Location = location,
        FolderName = "WinIDM Backups",
        MultipartMaxBytes = multipartMax,
        ChunkBytes = Chunk,
        PageSize = pageSize,
        Cloud = cloud ?? new CloudOptions { Clock = new FakeClock(CloudTest.Start) },
    };

    private static GoogleDriveDestination Drive(FakeDriveServer server, HttpClient http, GoogleDriveOptions? options = null) =>
        new(new FixedToken(), http, options ?? Options(server));

    [Fact]
    public async Task Small_upload_list_download_and_delete_round_trip()
    {
        using var server = new FakeDriveServer();
        using var http = CloudTest.NewHttp();
        using var dir = new TempDir();
        var drive = Drive(server, http);
        var name = BackupNames.NewName(CloudTest.Start, encrypted: true);
        var payload = CloudTest.RandomBytes(10_000, seed: 1);
        var source = dir.File("source.zip.enc");
        File.WriteAllBytes(source, payload);

        await drive.UploadAsync(source, name);

        var entry = Assert.Single(await drive.ListAsync());
        Assert.Equal(name, entry.Name);
        Assert.Equal(payload.Length, entry.SizeBytes);
        Assert.Contains(server.Requests, r => r.Method == "GET" && (r.QueryValue("q") ?? string.Empty).Contains("name contains 'winidm-backup-'", StringComparison.Ordinal));

        var copy = dir.File("copy.zip.enc");
        await drive.DownloadAsync(name, copy);
        Assert.Equal(payload, File.ReadAllBytes(copy));

        await drive.DeleteAsync(name);
        Assert.Empty(await drive.ListAsync());
        Assert.Null(server.ContentOf(name));
        await drive.DeleteAsync(name);
    }

    [Fact]
    public async Task Upload_of_existing_name_replaces_content_with_patch()
    {
        using var server = new FakeDriveServer();
        using var http = CloudTest.NewHttp();
        using var dir = new TempDir();
        var drive = Drive(server, http);
        var name = BackupNames.NewName(CloudTest.Start, encrypted: false);
        var first = dir.File("a.zip");
        var second = dir.File("b.zip");
        File.WriteAllText(first, "first-version");
        File.WriteAllText(second, "second-version");

        await drive.UploadAsync(first, name);
        await drive.UploadAsync(second, name);

        Assert.Equal(1, server.CountNamed(name));
        Assert.Equal("second-version", System.Text.Encoding.UTF8.GetString(server.ContentOf(name)!));
        Assert.Contains(server.Requests, r => r.Method == "PATCH");
    }

    [Fact]
    public async Task List_follows_next_page_token_until_every_page_is_read()
    {
        using var server = new FakeDriveServer { PageSize = 2 };
        using var http = CloudTest.NewHttp();
        using var dir = new TempDir();
        var drive = Drive(server, http, Options(server, pageSize: 2));
        var names = Enumerable.Range(1, 5)
            .Select(i => $"winidm-backup-20260101-00000{i}-000.zip")
            .ToList();
        foreach (var name in names)
        {
            var file = dir.File(name);
            File.WriteAllText(file, name);
            await drive.UploadAsync(file, name);
        }

        var listed = await drive.ListAsync();

        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), listed.Select(e => e.Name));
        Assert.True(server.Requests.Count(r => r.Method == "GET" && r.QueryValue("pageToken") is not null) >= 2);
    }

    [Fact]
    public async Task Large_upload_uses_resumable_session_in_chunks_and_reports_progress()
    {
        using var server = new FakeDriveServer();
        using var http = CloudTest.NewHttp();
        using var dir = new TempDir();
        var drive = Drive(server, http, Options(server, multipartMax: 64 * 1024));
        var name = BackupNames.NewName(CloudTest.Start, encrypted: true);
        var payload = CloudTest.RandomBytes(1024 * 1024 + 100, seed: 2);
        var source = dir.File("large.zip.enc");
        File.WriteAllBytes(source, payload);
        var progress = new RecordingProgress();

        await drive.UploadAsync(source, name, progress);

        Assert.Equal(payload, server.ContentOf(name));
        Assert.Equal(5, server.Requests.Count(CloudTest.IsDataPut));
        Assert.Contains(progress.Items, p => p.BytesTransferred == payload.Length);
        Assert.Contains(server.Requests, r => r.Method == "POST" && r.QueryValue("uploadType") == "resumable");
    }

    [Fact]
    public async Task Interrupted_resumable_upload_resumes_from_offset_confirmed_by_server()
    {
        using var server = new FakeDriveServer();
        using var http = CloudTest.NewHttp();
        using var dir = new TempDir();
        var drive = Drive(server, http, Options(server, multipartMax: 64 * 1024));
        var name = BackupNames.NewName(CloudTest.Start, encrypted: true);
        var payload = CloudTest.RandomBytes(3 * Chunk + 1000, seed: 3);
        var source = dir.File("interrupted.zip.enc");
        File.WriteAllBytes(source, payload);

        // The connection is dropped every time the third chunk is sent, until the retries run out.
        var failingStart = 2L * Chunk;
        server.AddFault(new FakeFault(r => CloudTest.IsDataPut(r) && CloudTest.RangeStart(r) == failingStart,
            FaultKind.AbortBeforeApply, times: 5));

        await drive.UploadAsync(source, name);

        Assert.Equal(payload, server.ContentOf(name));
        Assert.Contains(server.Requests, r => r.Method == "PUT" && (r.Header("Content-Range") ?? string.Empty).Contains('*', StringComparison.Ordinal));
        Assert.True(server.Requests.Count(r => CloudTest.IsDataPut(r) && CloudTest.RangeStart(r) == failingStart) >= 6);
    }

    [Fact]
    public async Task Cancelling_during_resumable_upload_stops_and_leaves_no_file()
    {
        using var server = new FakeDriveServer();
        using var http = CloudTest.NewHttp();
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        var drive = Drive(server, http, Options(server, multipartMax: 64 * 1024));
        var name = BackupNames.NewName(CloudTest.Start, encrypted: true);
        var source = dir.File("cancel.zip.enc");
        File.WriteAllBytes(source, CloudTest.RandomBytes(4 * Chunk, seed: 4));

        // Cancellation is triggered while the second chunk reaches the server.
        server.AddFault(new FakeFault(r =>
        {
            if (CloudTest.IsDataPut(r) && CloudTest.RangeStart(r) == Chunk)
            {
                cts.Cancel();
            }

            return false;
        }, FaultKind.Respond, times: int.MaxValue, response: FakeResponse.Empty(500)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drive.UploadAsync(source, name, cts.Token));
        Assert.Null(server.ContentOf(name));
    }

    [Fact]
    public async Task Named_folder_is_created_on_upload_and_listing_only_shows_its_own_files()
    {
        using var server = new FakeDriveServer();
        using var http = CloudTest.NewHttp();
        using var dir = new TempDir();
        server.SeedFile("winidm-backup-20250101-000000-000.zip", new byte[] { 1 });
        var named = Drive(server, http, Options(server, GoogleDriveLocation.NamedFolder));
        var name = BackupNames.NewName(CloudTest.Start, encrypted: false);
        var source = dir.File("source.zip");
        File.WriteAllText(source, "content");

        await named.UploadAsync(source, name);

        var folderId = server.FolderId("WinIDM Backups");
        Assert.NotNull(folderId);
        Assert.Equal(new[] { folderId! }, server.ParentsOf(name));
        Assert.Equal(new[] { name }, (await named.ListAsync()).Select(e => e.Name));
    }

    [Fact]
    public async Task Appdata_folder_uses_appdata_space_and_is_hidden_from_root_listing()
    {
        using var server = new FakeDriveServer();
        using var http = CloudTest.NewHttp();
        using var dir = new TempDir();
        var appData = Drive(server, http, Options(server, GoogleDriveLocation.AppDataFolder));
        var root = Drive(server, http, Options(server));
        var name = BackupNames.NewName(CloudTest.Start, encrypted: false);
        var source = dir.File("source.zip");
        File.WriteAllText(source, "appdata");

        await appData.UploadAsync(source, name);

        Assert.Equal(new[] { "appDataFolder" }, server.ParentsOf(name));
        Assert.Equal(new[] { name }, (await appData.ListAsync()).Select(e => e.Name));
        Assert.Empty(await root.ListAsync());
        Assert.Contains(server.Requests, r => r.QueryValue("spaces") == "appDataFolder");
    }

    [Fact]
    public async Task Download_of_missing_backup_throws_file_not_found()
    {
        using var server = new FakeDriveServer();
        using var http = CloudTest.NewHttp();
        using var dir = new TempDir();
        var drive = Drive(server, http);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            drive.DownloadAsync(BackupNames.NewName(CloudTest.Start, false), dir.File("missing.zip")));
    }

    [Fact]
    public void Chunk_size_must_be_a_multiple_of_256_kib()
    {
        using var server = new FakeDriveServer();
        var options = new GoogleDriveOptions
        {
            ApiBase = new Uri(server.BaseUri, "drive/v3/"),
            UploadBase = new Uri(server.BaseUri, "upload/drive/v3/"),
            ChunkBytes = 300 * 1024,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new GoogleDriveDestination(new FixedToken(), CloudTest.NewHttp(), options));
    }
}
