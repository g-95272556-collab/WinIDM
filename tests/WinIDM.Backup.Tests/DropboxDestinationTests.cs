using WinIDM.Backup;

namespace WinIDM.Backup.Tests;

public class DropboxDestinationTests
{
    private const int Chunk = 64 * 1024;

    private static DropboxOptions Options(FakeDropboxServer server, long singleMax = 128L * 1024 * 1024, int listPage = 2000) => new()
    {
        ApiBase = new Uri(server.BaseUri, "2/"),
        ContentBase = new Uri(server.BaseUri, "2/"),
        FolderPath = "/WinIDM Backups",
        SingleUploadMaxBytes = singleMax,
        SessionChunkBytes = Chunk,
        ListPageSize = listPage,
        Cloud = new CloudOptions { Clock = new FakeClock(CloudTest.Start) },
    };

    [Fact]
    public async Task Small_upload_list_download_and_delete_round_trip()
    {
        using var server = new FakeDropboxServer();
        using var http = CloudTest.NewHttp();
        using var dir = new TempDir();
        var dropbox = new DropboxDestination(new FixedToken(), http, Options(server));
        var name = BackupNames.NewName(CloudTest.Start, encrypted: true);
        var payload = CloudTest.RandomBytes(20_000, seed: 11);
        var source = dir.File("source.zip.enc");
        File.WriteAllBytes(source, payload);

        await dropbox.UploadAsync(source, name);

        var entry = Assert.Single(await dropbox.ListAsync());
        Assert.Equal(name, entry.Name);
        Assert.Equal(payload.Length, entry.SizeBytes);
        Assert.Equal(payload, server.ContentAt("/WinIDM Backups/" + name));

        var copy = dir.File("copy.zip.enc");
        await dropbox.DownloadAsync(name, copy);
        Assert.Equal(payload, File.ReadAllBytes(copy));

        await dropbox.DeleteAsync(name);
        await dropbox.DeleteAsync(name);
        Assert.Empty(await dropbox.ListAsync());
        await Assert.ThrowsAsync<FileNotFoundException>(() => dropbox.DownloadAsync(name, dir.File("missing.zip")));
    }

    [Fact]
    public async Task List_follows_has_more_and_continue_cursor()
    {
        using var server = new FakeDropboxServer { PageSize = 2 };
        using var http = CloudTest.NewHttp();
        using var dir = new TempDir();
        var dropbox = new DropboxDestination(new FixedToken(), http, Options(server, listPage: 2));
        var names = Enumerable.Range(1, 5).Select(i => $"winidm-backup-20260101-00000{i}-000.zip").ToList();
        foreach (var name in names)
        {
            var file = dir.File(name);
            File.WriteAllText(file, name);
            await dropbox.UploadAsync(file, name);
        }

        var listed = await dropbox.ListAsync();

        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), listed.Select(e => e.Name));
        Assert.True(server.Requests.Count(r => r.Path == "/2/files/list_folder/continue") >= 2);
    }

    [Fact]
    public async Task Large_upload_uses_upload_session_start_append_and_finish()
    {
        using var server = new FakeDropboxServer();
        using var http = CloudTest.NewHttp();
        using var dir = new TempDir();
        var dropbox = new DropboxDestination(new FixedToken(), http, Options(server, singleMax: 1000));
        var name = BackupNames.NewName(CloudTest.Start, encrypted: true);
        var payload = CloudTest.RandomBytes(300_000, seed: 12);
        var source = dir.File("large.zip.enc");
        File.WriteAllBytes(source, payload);
        var progress = new RecordingProgress();

        await dropbox.UploadAsync(source, name, progress);

        Assert.Equal(payload, server.ContentAt("/WinIDM Backups/" + name));
        Assert.Single(server.Requests, r => r.Path == "/2/files/upload_session/start");
        Assert.Single(server.Requests, r => r.Path == "/2/files/upload_session/finish");
        Assert.True(server.Requests.Count(r => r.Path == "/2/files/upload_session/append_v2") >= 3);
        Assert.Contains(progress.Items, p => p.BytesTransferred == payload.Length);
    }

    [Fact]
    public async Task Lost_append_response_resumes_at_correct_offset_reported_by_server()
    {
        using var server = new FakeDropboxServer();
        using var http = CloudTest.NewHttp();
        using var dir = new TempDir();
        var dropbox = new DropboxDestination(new FixedToken(), http, Options(server, singleMax: 1000));
        var name = BackupNames.NewName(CloudTest.Start, encrypted: true);
        var payload = CloudTest.RandomBytes(300_000, seed: 13);
        var source = dir.File("interrupted.zip.enc");
        File.WriteAllBytes(source, payload);

        // The server receives the chunk at offset 2*Chunk, but the response is lost: the client retries.
        var lostOffset = 2L * Chunk;
        server.AddFault(new FakeFault(r =>
                r.Path == "/2/files/upload_session/append_v2"
                && r.Header("Dropbox-API-Arg") is not null
                && CloudTest.DropboxArgOffset(r) == lostOffset,
            FaultKind.AbortAfterApply));

        await dropbox.UploadAsync(source, name);

        Assert.Equal(payload, server.ContentAt("/WinIDM Backups/" + name));
        Assert.True(server.Requests.Count(r => r.Path == "/2/files/upload_session/append_v2"
            && CloudTest.DropboxArgOffset(r) == lostOffset) >= 2);
    }

    [Fact]
    public async Task Upload_with_wrong_token_fails_without_leaking_it_into_the_message()
    {
        using var server = new FakeDropboxServer();
        using var http = CloudTest.NewHttp();
        using var dir = new TempDir();
        var source = dir.File("x.zip");
        File.WriteAllText(source, "x");
        var dropbox = new DropboxDestination(new FixedToken("at-wrong-only"), http, Options(server));

        var ex = await Assert.ThrowsAsync<BackupTransportException>(() =>
            dropbox.UploadAsync(source, BackupNames.NewName(CloudTest.Start, false)));

        Assert.Equal(401, ex.StatusCode);
        Assert.DoesNotContain("at-wrong-only", ex.ToString(), StringComparison.Ordinal);
    }
}
