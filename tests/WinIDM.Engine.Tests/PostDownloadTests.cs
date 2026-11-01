using System.Security.Cryptography;
using WinIDM.Core.Models;
using WinIDM.Engine;
using WinIDM.Engine.Tests.Support;

namespace WinIDM.Engine.Tests;

/// <summary>
/// Post-download actions: file moves, link removal, DownloadCompleted/DownloadFailed events,
/// queue completion, checksum from text, and link refresh. Servers are 127.0.0.1 only.
/// </summary>
public sealed class PostDownloadTests
{
    private const int Mib = 1024 * 1024;

    /// <summary>Serve several files on the local server; other paths return 404.</summary>
    private static Dictionary<string, VirtualFile> Host(LocalHttpServer server, params (string Path, byte[] Content)[] files)
    {
        var map = files.ToDictionary(f => f.Path, f => new VirtualFile(f.Content), StringComparer.Ordinal);
        server.Handler = req => map.TryGetValue(req.Target, out var file)
            ? file.Serve(req)
            : new ServerResponse { Status = 404, Reason = "Not Found" };
        return map;
    }

    private static string FinalPath(DownloadItem item) => Path.Combine(item.SaveDirectory, item.FileName);

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // ------------------------------------------------------------------
    // File moves
    // ------------------------------------------------------------------

    [Fact]
    public async Task Completed_MovesFileToConfiguredFolder_AndRaisesEvent()
    {
        await using var server = new LocalHttpServer();
        var content = TestData.Random(200 * 1024, seed: 61);
        Host(server, ("/report.bin", content));

        var dest = Path.Combine(Path.GetTempPath(), "winidm-tests", "move-" + Guid.NewGuid().ToString("N"));
        await using var h = new EngineHarness(s =>
        {
            s.Set(SettingKeys.MoveFilesOnFinish, true);
            s.Set(SettingKeys.MoveFilesPath, dest);
        });

        DownloadCompletedInfo? info = null;
        h.Engine.DownloadCompleted += i => info = i;

        var id = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/report.bin" });
        await h.WaitForStateAsync(id, DownloadState.Completed);
        await EngineHarness.WaitUntilAsync(() => info is not null, 10000, "DownloadCompleted event");

        Assert.Equal(Path.Combine(dest, "report.bin"), info!.FullPath);
        Assert.Equal(dest, info.MovedTo);
        Assert.Equal(content.Length, info.TotalBytes);
        Assert.True(File.Exists(Path.Combine(dest, "report.bin")));
        Assert.False(File.Exists(Path.Combine(h.SaveDirectory, "report.bin")));
        Assert.Equal(dest, (await h.GetItemAsync(id)).SaveDirectory);

        Directory.Delete(dest, recursive: true);
    }

    [Fact]
    public async Task MoveByCategory_PlacesVideoInCategoryFolder()
    {
        await using var server = new LocalHttpServer();
        Host(server, ("/clip.mp4", TestData.Random(64 * 1024, seed: 62)));

        var dest = Path.Combine(Path.GetTempPath(), "winidm-tests", "category-" + Guid.NewGuid().ToString("N"));
        await using var h = new EngineHarness(s =>
        {
            s.Set(SettingKeys.MoveFilesOnFinish, true);
            s.Set(SettingKeys.MoveFilesPath, dest);
            s.Set(SettingKeys.MoveFilesByCategory, true);
        });

        var id = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/clip.mp4" });
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);
        await EngineHarness.WaitUntilAsync(() => File.Exists(Path.Combine(dest, "Video", "clip.mp4")), 10000, "file moved");

        Assert.Equal(Path.Combine(dest, "Video"), (await h.GetItemAsync(id)).SaveDirectory);
        Assert.Equal("clip.mp4", (await h.GetItemAsync(id)).FileName);
        _ = item;

        Directory.Delete(dest, recursive: true);
    }

    [Fact]
    public async Task MoveRules_ExtensionRuleWinsOverCategory()
    {
        await using var server = new LocalHttpServer();
        var iso = TestData.Random(32 * 1024, seed: 63);
        var zip = TestData.Random(32 * 1024, seed: 64);
        Host(server, ("/package.iso", iso), ("/archive.zip", zip));

        var root = Path.Combine(Path.GetTempPath(), "winidm-tests", "rules-" + Guid.NewGuid().ToString("N"));
        var isoDir = Path.Combine(root, "iso");
        var arcDir = Path.Combine(root, "archive");
        var rules = $"[{{\"extension\":\".iso\",\"folder\":\"{isoDir.Replace("\\", "\\\\")}\"}}," +
                    $"{{\"category\":\"Archive\",\"folder\":\"{arcDir.Replace("\\", "\\\\")}\"}}]";

        await using var h = new EngineHarness(s => s.Set(SettingKeys.MoveRules, rules));

        var isoId = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/package.iso" });
        var zipId = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/archive.zip" });
        await h.WaitForStateAsync(isoId, DownloadState.Completed);
        await h.WaitForStateAsync(zipId, DownloadState.Completed);
        await EngineHarness.WaitUntilAsync(() =>
            File.Exists(Path.Combine(isoDir, "package.iso")) && File.Exists(Path.Combine(arcDir, "archive.zip")),
            10000, "file moved by rules");

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task MoveCollision_DoesNotOverwriteExistingFile()
    {
        await using var server = new LocalHttpServer();
        var content = TestData.Random(48 * 1024, seed: 65);
        Host(server, ("/report.bin", content));

        var dest = Path.Combine(Path.GetTempPath(), "winidm-tests", "conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dest);
        await File.WriteAllTextAsync(Path.Combine(dest, "report.bin"), "old file, do not overwrite");

        await using var h = new EngineHarness(s =>
        {
            s.Set(SettingKeys.MoveFilesOnFinish, true);
            s.Set(SettingKeys.MoveFilesPath, dest);
        });

        var id = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/report.bin" });
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);
        await EngineHarness.WaitUntilAsync(() => Directory.GetFiles(dest).Length == 2, 10000, "file moved");

        var moved = await h.GetItemAsync(id);
        Assert.Equal("old file, do not overwrite", await File.ReadAllTextAsync(Path.Combine(dest, "report.bin")));
        Assert.NotEqual("report.bin", moved.FileName);
        Assert.Equal(Sha256(content), Sha256(File.ReadAllBytes(FinalPath(moved))));
        Assert.Equal(DownloadState.Completed, item.State);

        Directory.Delete(dest, recursive: true);
    }

    // ------------------------------------------------------------------
    // Link removal
    // ------------------------------------------------------------------

    [Fact]
    public async Task RemoveLinkOnFinish_DeletesRecord_KeepsFile()
    {
        await using var server = new LocalHttpServer();
        Host(server, ("/note.txt", TestData.Random(10 * 1024, seed: 66)));

        await using var h = new EngineHarness(s => s.Set(SettingKeys.RemoveLinkOnFinish, true));

        DownloadCompletedInfo? info = null;
        h.Engine.DownloadCompleted += i => info = i;

        var id = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/note.txt" });
        await EngineHarness.WaitUntilAsync(() => info is not null, 10000, "DownloadCompleted event");

        Assert.Null(await h.Repository.GetAsync(id));
        Assert.True(File.Exists(info!.FullPath));
    }

    [Fact]
    public async Task RemoveOldLinks_RemovesCompletedOlderThanDays_KeepsFiles()
    {
        await using var server = new LocalHttpServer();
        Host(server, ("/old.bin", TestData.Random(16 * 1024, seed: 67)));

        await using var h = new EngineHarness(s =>
        {
            s.Set(SettingKeys.DeleteLinksAfterDays, 7);
            s.Set(SettingKeys.DeleteLinksOnlyFinished, true);
        });

        var id = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/old.bin" });
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(0, await h.Engine.RemoveOldLinksAsync(DateTimeOffset.UtcNow.AddDays(1)));
        Assert.NotNull(await h.Repository.GetAsync(id));

        Assert.Equal(1, await h.Engine.RemoveOldLinksAsync(DateTimeOffset.UtcNow.AddDays(8)));
        Assert.Null(await h.Repository.GetAsync(id));
        Assert.True(File.Exists(FinalPath(item)), "The file is not deleted when the link is removed.");
    }

    [Fact]
    public async Task RemoveOldLinks_DisabledByDefault_RemovesNothing()
    {
        await using var server = new LocalHttpServer();
        Host(server, ("/file.bin", TestData.Random(8 * 1024, seed: 68)));

        await using var h = new EngineHarness();
        var id = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/file.bin" });
        await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(0, await h.Engine.RemoveOldLinksAsync(DateTimeOffset.UtcNow.AddYears(10)));
        Assert.NotNull(await h.Repository.GetAsync(id));
    }

    // ------------------------------------------------------------------
    // Failure and queue-completion events
    // ------------------------------------------------------------------

    [Fact]
    public async Task Failure_RaisesDownloadFailedEvent_WithHttpCode()
    {
        await using var server = new LocalHttpServer();
        Host(server);

        await using var h = new EngineHarness(s => s.Set(SettingKeys.Retry, false));

        DownloadFailedInfo? failed = null;
        h.Engine.DownloadFailed += f => failed = f;

        var id = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/missing.bin" });
        await h.WaitForStateAsync(id, DownloadState.Error);
        await EngineHarness.WaitUntilAsync(() => failed is not null, 10000, "DownloadFailed event");

        Assert.Equal(id, failed!.Id);
        Assert.Equal(404, failed.ErrorCode);
    }

    [Fact]
    public async Task AfterDownloadsFinished_RaisedOnce_WithConfiguredAction_AndCounts()
    {
        await using var server = new LocalHttpServer();
        Host(server, ("/one.bin", TestData.Random(24 * 1024, seed: 69)), ("/two.bin", TestData.Random(24 * 1024, seed: 70)));

        await using var h = new EngineHarness(s => s.Set(SettingKeys.AfterDownloadsAction, SystemAction.Shutdown));

        var requests = new List<SystemActionRequest>();
        h.Engine.AfterDownloadsFinished += r => requests.Add(r);

        var a = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/one.bin" });
        var b = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/two.bin" });
        await h.WaitForStateAsync(a, DownloadState.Completed);
        await h.WaitForStateAsync(b, DownloadState.Completed);
        await EngineHarness.WaitUntilAsync(() => requests.Count >= 1, 10000, "AfterDownloadsFinished event");
        await Task.Delay(200);

        Assert.Single(requests);
        Assert.Equal(SystemAction.Shutdown, requests[0].Action);
        Assert.Equal(2, requests[0].CompletedCount);
        Assert.Equal(0, requests[0].FailedCount);
    }

    // ------------------------------------------------------------------
    // Checksum from text
    // ------------------------------------------------------------------

    [Fact]
    public void ChecksumText_Parse_PrefersMatchingFileName_AndAcceptsBinaryMarker()
    {
        var sha1 = new string('a', 64);
        var sha2 = new string('b', 64);
        var text = $"# checksum\n{sha1}  other.bin\n{sha2} *report.bin\n";

        var parsed = ChecksumText.Parse(text, "report.bin");

        Assert.NotNull(parsed);
        Assert.Equal("sha256", parsed!.Value.Algorithm);
        Assert.Equal(sha2, parsed.Value.Hex);
    }

    [Fact]
    public void ChecksumText_Parse_DetectsMd5AndBareHash_AndRejectsGarbage()
    {
        var md5 = new string('c', 32);
        Assert.Equal(("md5", md5), ChecksumText.Parse(md5, "x.bin"));
        Assert.Null(ChecksumText.Parse("not a checksum at all", "x.bin"));
        Assert.Null(ChecksumText.Parse(new string('z', 64), "x.bin"));
    }

    [Fact]
    public async Task SetChecksumFromText_Mismatch_ErrorsWithChecksumCode()
    {
        await using var server = new LocalHttpServer();
        var content = TestData.Random(40 * 1024, seed: 71);
        Host(server, ("/report.bin", content));

        await using var h = new EngineHarness();
        var id = await h.Engine.AddAsync(new DownloadRequest
        {
            Url = server.BaseUrl + "/report.bin",
            FileName = "report.bin",
            StartImmediately = false,
        });

        await h.Engine.SetChecksumFromTextAsync(id, $"{new string('0', 64)}  report.bin");
        await h.Engine.ResumeAsync(id);
        var item = await h.WaitForStateAsync(id, DownloadState.Error);

        Assert.Equal(EngineErrorCodes.ChecksumMismatch, item.ErrorCode);
    }

    [Fact]
    public async Task SetChecksumFromText_Match_Completes()
    {
        await using var server = new LocalHttpServer();
        var content = TestData.Random(40 * 1024, seed: 72);
        Host(server, ("/report.bin", content));

        await using var h = new EngineHarness();
        var id = await h.Engine.AddAsync(new DownloadRequest
        {
            Url = server.BaseUrl + "/report.bin",
            FileName = "report.bin",
            StartImmediately = false,
        });

        await h.Engine.SetChecksumFromTextAsync(id, $"{Sha256(content)}  report.bin");
        await h.Engine.ResumeAsync(id);
        await h.WaitForStateAsync(id, DownloadState.Completed);
    }

    // ------------------------------------------------------------------
    // Link refresh
    // ------------------------------------------------------------------

    [Fact]
    public async Task RefreshLink_SameSize_KeepsSegmentsAndResumesOnNewUrl()
    {
        await using var server = new LocalHttpServer();
        var content = TestData.Random(6 * Mib, seed: 73);
        Host(server, ("/old.bin", content), ("/new.bin", content));

        // Slow the response so the download runs partially before the link is switched.
        var inner = server.Handler;
        server.Handler = req =>
        {
            var response = inner(req);
            if (req.Target == "/old.bin" && req.Range is { To: not 0 })
            {
                return new ServerResponse
                {
                    Status = response.Status,
                    Reason = response.Reason,
                    Body = response.Body,
                    ChunkDelayMs = 25,
                }.WithCopiedHeaders(response);
            }

            return response;
        };

        await using var h = new EngineHarness();
        var id = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/old.bin" });
        await EngineHarness.WaitUntilAsync(async () => (await h.Repository.GetAsync(id))?.DownloadedBytes > 256 * 1024,
            20000, "partial progress");

        await h.Engine.RefreshLinkAsync(id, server.BaseUrl + "/new.bin");
        await h.Engine.ResumeAsync(id);
        var item = await h.WaitForStateAsync(id, DownloadState.Completed);

        Assert.Equal(Sha256(content), Sha256(File.ReadAllBytes(FinalPath(item))));
        Assert.Contains(server.Requests, r => r.Target == "/new.bin" && r.Range is { From: > 0 });
        Assert.Equal(server.BaseUrl + "/new.bin", item.Url);
    }

    [Fact]
    public async Task RefreshLink_RejectsNonHttpScheme_AndCompletedDownloads()
    {
        await using var server = new LocalHttpServer();
        Host(server, ("/file.bin", TestData.Random(4 * 1024, seed: 74)));

        await using var h = new EngineHarness();
        var id = await h.Engine.AddAsync(new DownloadRequest { Url = server.BaseUrl + "/file.bin" });
        await h.WaitForStateAsync(id, DownloadState.Completed);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Engine.RefreshLinkAsync(id, server.BaseUrl + "/other.bin"));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Engine.RefreshLinkAsync(id, "ftp://example.invalid/file.bin"));
    }
}

/// <summary>Test helper: copy headers from an existing response.</summary>
internal static class ServerResponseCopyExtensions
{
    public static ServerResponse WithCopiedHeaders(this ServerResponse target, ServerResponse source)
    {
        foreach (var (name, value) in source.Headers) target.Headers[name] = value;
        return target;
    }
}
