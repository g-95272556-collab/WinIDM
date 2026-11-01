using System.Diagnostics;
using MonoTorrent;
using MonoTorrent.Client;
using WinIDM.Core.Models;
using WinIDM.Torrent.Tests.Support;

namespace WinIDM.Torrent.Tests;

/// <summary>
/// Real integration tests: a TorrentService seeder and leecher on different loopback ports, connected
/// through a local HTTP tracker written in the test. DHT, LSD, UPnP, and NAT-PMP are off; no
/// connection to an external host.
/// </summary>
/// <remarks>
/// The rate-limit test is moved to <see cref="RateLimits_HonouredPerTorrent_InIsolatedProcess"/>: MonoTorrent 3.0.2
/// honours rate limits only on the first throttled engine in a process (see the README). That test re-runs
/// itself in a child process so it is always the first engine.
/// </remarks>
[Collection("Torrent integration")]
public sealed class TorrentIntegrationTests
{
    private const int KiB = 1024;
    private const int MiB = 1024 * 1024;
    private const string IsolatedEnv = "WINIDM_RATE_ISOLATED";

    [Fact]
    public async Task MultiFileSwarm_DownloadsEveryFileWithMatchingSha256AndReportsCompletion()
    {
        await using var run = new SwarmRun();
        var torrent = await run.CreateContentAsync(
            "integration-package",
            ("alpha.bin", 4 * MiB + 1000),
            ("docs/beta.bin", 3 * MiB),
            ("readme.txt", 300));

        var seeder = run.CreatePeer("seeder", seedFinished: true);
        await run.AddTorrentAsync(seeder, torrent, torrent.ContentRoot);
        await run.WaitForTrackerPeersAsync(torrent);

        var leecher = run.CreatePeer("leecher", seedFinished: false);
        await run.AddTorrentAsync(leecher, torrent, run.DownloadsOf("leecher"));
        await run.WaitForStateAsync(leecher, DownloadState.Completed);

        foreach (var file in torrent.Files)
        {
            var downloaded = torrent.DownloadPathOf(run.DownloadsOf("leecher"), file);
            Assert.Equal(file.Sha256, await IntegrationKit.Sha256FileAsync(downloaded));
        }

        // Progress reports: never decreasing and reaching completion with the correct total size.
        await run.WaitForReportAsync(leecher,
            r => r.State == DownloadState.Completed && r.DownloadedBytes == torrent.TotalLength,
            "ProgressChanged Completed report with the full byte count");
        var reports = leecher.Reports.Where(r => r.Id == leecher.Id).ToList();
        Assert.True(reports.Zip(reports.Skip(1)).All(p => p.Second.DownloadedBytes >= p.First.DownloadedBytes),
            "DownloadedBytes must not decrease during a normal download");
        Assert.All(reports, r => Assert.Equal(torrent.TotalLength, r.TotalBytes));

        // The seeder actually sends data (not merely connects).
        Assert.True(seeder.Manager.Monitor.DataBytesSent >= torrent.TotalLength,
            $"the seeder only sent {seeder.Manager.Monitor.DataBytesSent} bytes");

        // seed_finished_torrent = false: the leecher stops by itself after finishing.
        await IntegrationKit.WaitUntilAsync(() => leecher.Manager.State == TorrentState.Stopped,
            "the leecher stops after finishing (seed_finished_torrent = false)");
        Assert.All(leecher.Service.GetFiles(leecher.Id), f => Assert.Equal(100.0, f.Progress, precision: 6));
    }

    [Fact]
    public async Task Seeding_WhenEnabled_CompletedLeecherKeepsServingAfterOriginalSeederLeaves()
    {
        await using var run = new SwarmRun();
        var torrent = await run.CreateContentAsync("seeding-package", ("data.bin", 3 * MiB), ("note.txt", 2000));

        var original = run.CreatePeer("original-seeder", seedFinished: true);
        await run.AddTorrentAsync(original, torrent, torrent.ContentRoot);
        await run.WaitForTrackerPeersAsync(torrent);

        var relay = run.CreatePeer("relay", seedFinished: true);
        await run.AddTorrentAsync(relay, torrent, run.DownloadsOf("relay"));
        await run.WaitForStateAsync(relay, DownloadState.Completed);

        // With seed_finished_torrent = true, the relay stays in the Seeding state.
        await Task.Delay(1500);
        Assert.Equal(TorrentState.Seeding, relay.Manager.State);
        Assert.Equal(DownloadState.Completed, relay.State);

        // The original seeder leaves; the tracker must remove it before the late leecher starts.
        await original.Service.PauseAsync(original.Id);
        await IntegrationKit.WaitUntilAsync(() => run.Tracker.PeerCount(torrent.InfoHash) == 1,
            "the tracker removes the original seeder after it stops");

        var late = run.CreatePeer("late-leecher", seedFinished: false);
        await run.AddTorrentAsync(late, torrent, run.DownloadsOf("late-leecher"));
        await run.WaitForStateAsync(late, DownloadState.Completed);

        foreach (var file in torrent.Files)
        {
            Assert.Equal(file.Sha256, await IntegrationKit.Sha256FileAsync(torrent.DownloadPathOf(run.DownloadsOf("late-leecher"), file)));
        }
        Assert.True(relay.Manager.Monitor.DataBytesSent >= torrent.TotalLength,
            "the relay must send data to the late leecher");
    }

    [Fact]
    public async Task ForceRecheck_AfterCorruptingOnePiece_DetectsItAndRedownloadsOnResume()
    {
        await using var run = new SwarmRun();
        var torrent = await run.CreateContentAsync("check-package", ("alpha.bin", 2 * MiB), ("beta.bin", 1 * MiB + 77));

        var seeder = run.CreatePeer("seeder", seedFinished: true);
        await run.AddTorrentAsync(seeder, torrent, torrent.ContentRoot);
        await run.WaitForTrackerPeersAsync(torrent);

        var leecher = run.CreatePeer("leecher", seedFinished: false);
        var id = await run.AddTorrentAsync(leecher, torrent, run.DownloadsOf("leecher"));
        await run.WaitForStateAsync(leecher, DownloadState.Completed);

        // Pause first so the recheck does not re-download immediately.
        await leecher.Service.PauseAsync(id);

        // Corrupt 16 bytes in piece 5 of alpha.bin (the first file; torrent offset = file offset).
        const int corruptPiece = 5;
        var alphaPath = torrent.DownloadPathOf(run.DownloadsOf("leecher"), torrent.Files[0]);
        await using (var fs = new FileStream(alphaPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            var offset = (long)corruptPiece * torrent.PieceLength + 100;
            fs.Seek(offset, SeekOrigin.Begin);
            var original = new byte[16];
            _ = await fs.ReadAsync(original);
            var corrupted = original.Select(b => (byte)(b ^ 0xFF)).ToArray();
            fs.Seek(offset, SeekOrigin.Begin);
            await fs.WriteAsync(corrupted);
        }
        Assert.NotEqual(torrent.Files[0].Sha256, await IntegrationKit.Sha256FileAsync(alphaPath));

        await leecher.Service.RecheckAsync(id);

        var afterRecheck = leecher.Service.GetStats(id);
        Assert.True(afterRecheck.Progress < 100, "recheck did not detect the corrupt piece");
        Assert.Equal(DownloadState.Paused, afterRecheck.State);
        Assert.True(leecher.Service.GetFiles(id)[0].Progress < 100, "the corrupt file is still 100%");
        Assert.Equal(100.0, leecher.Service.GetFiles(id)[1].Progress, precision: 6);

        // Resume: the missing piece is downloaded from the seeder.
        await leecher.Service.ResumeAsync(id);
        await run.WaitForStateAsync(leecher, DownloadState.Completed);
        foreach (var file in torrent.Files)
        {
            Assert.Equal(file.Sha256, await IntegrationKit.Sha256FileAsync(torrent.DownloadPathOf(run.DownloadsOf("leecher"), file)));
        }
    }

    [Fact]
    public async Task IgnoredFile_IsNeverDownloaded_WhileOtherFilesComplete()
    {
        await using var run = new SwarmRun();
        // TorrentCreator sorts files alphabetically; "zeta.bin" is the last file with a size that is a multiple of 64 KiB
        // except for 333 bytes, so its exclusive piece is not shared with the other files.
        var torrent = await run.CreateContentAsync(
            "ignore-package",
            ("alpha.bin", 3 * MiB),
            ("beta.bin", 1 * MiB),
            ("zeta.bin", 1 * MiB + 333));
        var ignored = torrent.Files.ToList().FindIndex(f => f.RelativePath == "zeta.bin");
        Assert.True(ignored >= 0);

        var leecher = run.CreatePeer("leecher", seedFinished: false);
        var id = await run.AddTorrentPausedAsync(leecher, torrent, run.DownloadsOf("leecher"));
        await leecher.Service.SetFilePriorityAsync(id, ignored, TorrentFilePriority.Ignore);
        Assert.Equal(TorrentFilePriority.Ignore, leecher.Service.GetFiles(id)[ignored].Priority);

        var seeder = run.CreatePeer("seeder", seedFinished: true);
        await run.AddTorrentAsync(seeder, torrent, torrent.ContentRoot);
        await run.WaitForTrackerPeersAsync(torrent);

        await leecher.Service.ResumeAsync(id);
        var wanted = Enumerable.Range(0, torrent.Files.Count).Where(i => i != ignored).ToList();
        await IntegrationKit.WaitUntilAsync(() =>
            wanted.All(i => leecher.Service.GetFiles(id)[i].Progress >= 100), "the files that are not ignored finish");

        var files = leecher.Service.GetFiles(id);
        Assert.Equal(0.0, files[ignored].Progress);
        Assert.Equal(TorrentFilePriority.Ignore, files[ignored].Priority);
        Assert.True(leecher.Service.GetStats(id).Progress < 100, "the torrent must not be 100% when a file is ignored");

        foreach (var i in wanted)
        {
            Assert.Equal(torrent.Files[i].Sha256, await IntegrationKit.Sha256FileAsync(torrent.DownloadPathOf(run.DownloadsOf("leecher"), torrent.Files[i])));
        }
    }

    [Fact]
    public async Task Magnet_FetchesMetadataFromSeederThroughTracker_WithoutTorrentFile()
    {
        await using var run = new SwarmRun();
        var torrent = await run.CreateContentAsync(
            "magnet-package",
            ("alpha.bin", 2 * MiB + 5),
            ("sub/beta.bin", 1 * MiB + 9),
            ("readme.txt", 200));

        var seeder = run.CreatePeer("seeder", seedFinished: true);
        await run.AddTorrentAsync(seeder, torrent, torrent.ContentRoot);
        await run.WaitForTrackerPeersAsync(torrent);

        // The leecher only knows the info hash and the tracker; no .torrent file is given to it.
        var magnet = $"magnet:?xt=urn:btih:{torrent.InfoHash}&dn={Uri.EscapeDataString(torrent.Name)}&tr={Uri.EscapeDataString(run.Tracker.AnnounceUrl)}";
        var leecher = run.CreatePeer("leecher", seedFinished: false, fetchMetadata: true);
        await leecher.Service.InitializeAsync();
        var id = await leecher.Service.AddMagnetAsync(magnet, run.DownloadsOf("leecher"));
        leecher.Id = id;
        leecher.Service.ProgressChanged += r => leecher.Reports.Enqueue(r);

        var stats = leecher.Service.GetStats(id);
        Assert.True(stats.HasMetadata, "magnet metadata was not fetched from the seeder");
        Assert.Equal(torrent.Name, stats.Name);
        Assert.Equal(torrent.InfoHash, stats.InfoHash);
        Assert.Equal(torrent.Files.Count, leecher.Service.GetFiles(id).Count);
        Assert.Equal(magnet, leecher.Service.GetMagnetLink(id));

        await run.WaitForStateAsync(leecher, DownloadState.Completed);
        foreach (var file in torrent.Files)
        {
            Assert.Equal(file.Sha256, await IntegrationKit.Sha256FileAsync(torrent.DownloadPathOf(run.DownloadsOf("leecher"), file)));
        }
    }

    [Fact]
    public async Task RestoreFailure_IsReportedAsErrorThroughProgressChanged()
    {
        await using var run = new SwarmRun();
        var torrent = await run.CreateContentAsync("error-package", ("file.bin", 300 * KiB), ("two.bin", 100 * KiB));

        var first = run.CreatePeer("first", seedFinished: false);
        var id = await run.AddTorrentAsync(first, torrent, run.DownloadsOf("first"));
        await first.Service.PauseAsync(id);
        await first.Service.DisposeAsync();

        // The .torrent copy is missing: restore at startup must fail clearly.
        File.Delete(Path.Combine(first.DataDirectory, "torrents", id + ".torrent"));

        var restarted = new TorrentService(
            OfflineSettings.Create().Put("use_random_port", false).Put("custom_port_number", IntegrationKit.ReserveFreePorts(1)[0]),
            first.DataDirectory);
        var reports = new System.Collections.Concurrent.ConcurrentQueue<DownloadProgress>();
        restarted.ProgressChanged += reports.Enqueue;
        try
        {
            await restarted.InitializeAsync();

            await IntegrationKit.WaitUntilAsync(
                () => reports.Any(r => r.Id == id && r.State == DownloadState.Error),
                "a ProgressChanged report with the Error state for the torrent that failed to restore",
                TimeSpan.FromSeconds(15));

            var stats = restarted.GetStats(id);
            Assert.Equal(DownloadState.Error, stats.State);
            Assert.False(string.IsNullOrEmpty(stats.LastError));
        }
        finally
        {
            await restarted.DisposeAsync();
        }
    }

    [Fact]
    public async Task RuntimeSettings_ApplyWithoutAppRestart_KeepCompletedStateAndData()
    {
        await using var run = new SwarmRun();
        var torrent = await run.CreateContentAsync("settings-package", ("one.bin", 2 * MiB), ("two.bin", 1 * MiB));

        var seeder = run.CreatePeer("seeder", seedFinished: true);
        await run.AddTorrentAsync(seeder, torrent, torrent.ContentRoot);
        await run.WaitForTrackerPeersAsync(torrent);

        var leecher = run.CreatePeer("leecher", seedFinished: false);
        var id = await run.AddTorrentAsync(leecher, torrent, run.DownloadsOf("leecher"));
        await run.WaitForStateAsync(leecher, DownloadState.Completed);

        // Change limits and priorities after the torrent has finished: data and state must persist.
        await leecher.Service.SetSpeedLimitsAsync(id, 2 * MiB, 1 * MiB);
        await leecher.Service.SetGlobalSpeedLimitsAsync(4 * MiB, 3 * MiB);
        await leecher.Service.SetFilePriorityAsync(id, 1, TorrentFilePriority.High);

        Assert.Equal(2 * MiB, leecher.Manager.Settings.MaximumDownloadRate);
        Assert.Equal(1 * MiB, leecher.Manager.Settings.MaximumUploadRate);
        Assert.Equal(4 * MiB, leecher.Service.Engine!.Settings.MaximumDownloadRate);
        Assert.Equal(3 * MiB, leecher.Service.Engine!.Settings.MaximumUploadRate);
        Assert.Equal(TorrentFilePriority.High, leecher.Service.GetFiles(id)[1].Priority);

        await run.WaitForStateAsync(leecher, DownloadState.Completed);
        Assert.Equal(100.0, leecher.Service.GetStats(id).Progress, precision: 6);
        foreach (var file in torrent.Files)
        {
            Assert.Equal(file.Sha256, await IntegrationKit.Sha256FileAsync(torrent.DownloadPathOf(run.DownloadsOf("leecher"), file)));
        }
    }

    /// <summary>
    /// Per-torrent speed limits are tested in a child process. This test calls itself with
    /// <see cref="IsolatedEnv"/>=1 through <c>dotnet test --no-build</c>; in the child process all scenarios
    /// run on one throttled engine (the first throttled engine in the process).
    /// </summary>
    [Fact]
    public async Task RateLimits_HonouredPerTorrent_InIsolatedProcess()
    {
        if (Environment.GetEnvironmentVariable(IsolatedEnv) == "1")
        {
            await RunRateScenariosAsync();
            return;
        }

        await RunInIsolatedProcessAsync();
    }

    private static async Task RunRateScenariosAsync()
    {
        const long limit = 512 * KiB;
        await using var run = new SwarmRun();

        var t1 = await run.CreateContentAsync("rate-pause-resume", ("a1.bin", 2 * MiB), ("a2.bin", 1 * MiB));
        var t3 = await run.CreateContentAsync("rate-concurrent-a", ("c1.bin", 2 * MiB), ("c2.bin", 1 * MiB));
        var t4 = await run.CreateContentAsync("rate-concurrent-b", ("d1.bin", 2 * MiB), ("d2.bin", 1 * MiB));

        var seeder = run.CreatePeer("seeder", seedFinished: true);
        foreach (var t in new[] { t1, t3, t4 })
        {
            await run.AddTorrentAsync(seeder, t, t.ContentRoot);
            await run.WaitForTrackerPeersAsync(t);
        }

        // The leecher stays in Seeding after finishing: stopping a finished manager can pollute the speed limit
        // of other torrents in the same engine (see the README), so there is no stop-after-finish here.
        var leecher = run.CreatePeer("leecher", seedFinished: true);
        var dl = run.DownloadsOf("leecher");

        // 1) The limit is set while paused, then resumed: the average rate must be capped.
        var id1 = await run.AddTorrentPausedAsync(leecher, t1, dl);
        await leecher.Service.SetSpeedLimitsAsync(id1, limit, 0);
        var clock1 = Stopwatch.StartNew();
        await leecher.Service.ResumeAsync(id1);
        await WaitForCompletedAsync(leecher, id1, TimeSpan.FromSeconds(90));
        var seconds1 = clock1.Elapsed.TotalSeconds;
        AssertRateCapped(t1, seconds1, limit, "pause-resume");

        // 3) Two throttled torrents at the same time in one engine: each must be capped on its own.
        var id3 = await run.AddTorrentPausedAsync(leecher, t3, dl);
        var id4 = await run.AddTorrentPausedAsync(leecher, t4, dl);
        await leecher.Service.SetSpeedLimitsAsync(id3, limit, 0);
        await leecher.Service.SetSpeedLimitsAsync(id4, limit, 0);
        var clock34 = Stopwatch.StartNew();
        await leecher.Service.ResumeAsync(id3);
        await leecher.Service.ResumeAsync(id4);
        await WaitForCompletedAsync(leecher, id3, TimeSpan.FromSeconds(90));
        await WaitForCompletedAsync(leecher, id4, TimeSpan.FromSeconds(90));
        var seconds34 = clock34.Elapsed.TotalSeconds;
        AssertRateCapped(t3, seconds34, limit, "concurrent-a");
        AssertRateCapped(t4, seconds34, limit, "concurrent-b");

        foreach (var (id, t) in new[] { (id1, t1), (id3, t3), (id4, t4) })
        {
            Assert.Equal(DownloadState.Completed, leecher.Service.GetStats(id).State);
            foreach (var file in t.Files)
            {
                Assert.Equal(file.Sha256, await IntegrationKit.Sha256FileAsync(t.DownloadPathOf(dl, file)));
            }
        }
    }

    /// <summary>The MonoTorrent manager for a given torrent in the leecher engine.</summary>
    private static MonoTorrent.Client.TorrentManager ManagerOf(Peer peer, SourceTorrent torrent) =>
        peer.Service.Engine!.Torrents.Single(m => m.InfoHashes.V1OrV2.ToHex().Equals(torrent.InfoHash, StringComparison.OrdinalIgnoreCase));

    private static async Task WaitForCompletedAsync(Peer peer, long id, TimeSpan timeout)
    {
        await IntegrationKit.WaitUntilAsync(
            () => peer.Service.GetStats(id).State == DownloadState.Completed,
            $"torrent {id} reaches Completed (now {peer.Service.GetStats(id).State}, {peer.Service.GetStats(id).Progress:F1}%)",
            timeout).ConfigureAwait(false);
    }

    /// <summary>
    /// The average rate must be capped. The initial burst is one second of the limit (512 KiB), so the minimum time is
    /// (size - limit) / limit, and the average rate may slightly exceed the limit.
    /// Both bounds are deliberately loose: this test measures wall-clock time in a child process, so a loaded machine
    /// (several test projects in parallel) stretches the measurement. A rate limit that is not wired up at all is still
    /// caught: 3 MiB would finish in well under a second, far outside these bounds.
    /// </summary>
    private static void AssertRateCapped(SourceTorrent torrent, double seconds, long limit, string label)
    {
        var minimumSeconds = (torrent.TotalLength - limit) / (double)limit;
        var averageRate = torrent.TotalLength / seconds;
        Assert.True(seconds >= minimumSeconds * 0.8,
            $"[{label}] download too fast: {seconds:F2} s (minimum ~{minimumSeconds:F1} s)");
        Assert.True(averageRate <= limit * 1.5,
            $"[{label}] average rate {averageRate / KiB:F0} KiB/s exceeds the limit {limit / KiB} KiB/s");
    }

    private static async Task RunInIsolatedProcessAsync()
    {
        var project = FindProjectFile();
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var psi = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add(project);
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--filter");
        psi.ArgumentList.Add("FullyQualifiedName~RateLimits_HonouredPerTorrent_InIsolatedProcess");
        psi.Environment[IsolatedEnv] = "1";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the rate-limit test child process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var exited = await Task.Run(() => process.WaitForExit(TimeSpan.FromMinutes(4))).ConfigureAwait(false);
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The rate-limit test child process timed out.");
        }

        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0 && output.Contains("Passed!", StringComparison.Ordinal),
            "The rate-limit test in the isolated process failed:\n" + Tail(output));
    }

    private static string FindProjectFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "WinIDM.Torrent.Tests.csproj");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("WinIDM.Torrent.Tests.csproj not found from " + AppContext.BaseDirectory);
    }

    private static string Tail(string text)
    {
        const int max = 3000;
        return text.Length <= max ? text : text[^max..];
    }
}
