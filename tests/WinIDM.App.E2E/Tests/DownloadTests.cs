using System.Windows.Automation;
using WinIDM.App.E2E.Support;

namespace WinIDM.App.E2E.Tests;

/// <summary>Scenarios 2 to 4: real downloads through the Add URL dialog, pause/resume, context menu and filters.</summary>
[Trait("Category", "E2E")]
public sealed class DownloadTests
{
    private const int MiB = 1024 * 1024;

    [Fact]
    public void S02_AddUrl_DownloadCompletes_HashMatches_RowMovesToFinishedTab()
    {
        var data = AppSession.NewTempDataDir();
        using var server = FixtureServer.Start();
        try
        {
            var payload = FixtureServer.Deterministic(2 * MiB, seed: 1001);
            var expected = FixtureServer.Sha256Hex(payload);
            const string name = "fixture-2mib.bin";
            server.Add("/files/" + name, payload);

            using var app = AppSession.Launch(data);
            Shots.RunScenario(app, "S02-add-url", () =>
            {
                AppFlows.AddUrl(app, server.Url("/files/" + name), parts: 4);

                AppFlows.WaitTabCount(app, "Completed", 1);

                var path = Path.Combine(app.DownloadDir, name);
                Assert.True(File.Exists(path), $"File does not exist: {path}");
                Assert.Equal(payload.Length, new FileInfo(path).Length);
                Assert.Equal(expected, FixtureServer.Sha256File(path));

                AppFlows.ClickTab(app, "Completed");
                Assert.NotNull(AppFlows.RowName(app, name));
                Assert.Equal(1, Uia.TabCount(app.Main, "All"));
            });
        }
        finally
        {
            AppSession.TryDeleteDataDir(data);
        }
    }

    [Fact]
    public void S03_PauseAndResume_SlowLargeDownload_FinalHashMatches()
    {
        var data = AppSession.NewTempDataDir();
        using var server = FixtureServer.Start();
        try
        {
            var payload = FixtureServer.Deterministic(4 * MiB, seed: 2002);
            var expected = FixtureServer.Sha256Hex(payload);
            const string name = "slow-4mib.bin";
            const string route = "/files/" + name;
            // 256 KiB/s per connection: about 8 seconds with 2 segments, enough to pause midway.
            server.Add(route, payload, bytesPerSecond: 256 * 1024);

            using var app = AppSession.Launch(data);
            Shots.RunScenario(app, "S03-pause-resume", () =>
            {
                AppFlows.AddUrl(app, server.Url(route), parts: 2);

                // Wait until about 1 MiB has been written to the socket.
                Uia.WaitUntil(() => server.LiveBytes(route) >= 1 * MiB, "1 MiB sent", TimeSpan.FromSeconds(40));

                AppFlows.ClickButton(app, "Pause all");
                AppFlows.WaitTabCount(app, "Inactive", 1);

                // Pause must really stop the traffic: the byte count does not grow after a short grace period.
                Thread.Sleep(1000);
                long pausedAt = server.LiveBytes(route);
                Thread.Sleep(2500);
                long afterWait = server.LiveBytes(route);
                Assert.True(afterWait - pausedAt <= 64 * 1024,
                    $"Traffic still flows after pause: +{afterWait - pausedAt} bytes in 2.5 seconds.");
                Assert.True(afterWait < payload.Length, "The download should not be complete when paused.");

                var resumeAt = DateTimeOffset.UtcNow;
                AppFlows.ClickButton(app, "Start all");
                AppFlows.WaitTabCount(app, "Completed", 1, TimeSpan.FromSeconds(90));

                var path = Path.Combine(app.DownloadDir, name);
                Assert.Equal(expected, FixtureServer.Sha256File(path));

                // Resume must use a Range request from the last position, not a re-download from byte 0.
                server.DumpLog(Path.Combine(RepoPaths.ArtifactsDir, "S03-requests.txt"));

                // A 1-byte probe (0-0) is allowed; real range requests starting at 0 after resume are forbidden.
                var restarted = server.Requests
                    .Where(r => r.Path == route && r.At > resumeAt && r.RangeStart == 0 && r.RangeEnd > 0)
                    .ToList();
                Assert.True(restarted.Count == 0,
                    $"Resume restarted from byte 0 ({restarted.Count} requests).");

                // The total bytes written must be about the file size (no re-download of finished segments).
                long totalWritten = server.LiveBytes(route);
                Assert.True(totalWritten <= payload.Length + 64 * 1024,
                    $"Bytes written {totalWritten} exceed the file size {payload.Length} (a re-download happened).");
            });
        }
        finally
        {
            AppSession.TryDeleteDataDir(data);
        }
    }

    [Fact]
    public void S04_RowContextMenu_GroupActions_Filters_Clipboard_Checksum()
    {
        var data = AppSession.NewTempDataDir();
        using var server = FixtureServer.Start();
        try
        {
            var alpha = FixtureServer.Deterministic(1 * MiB, seed: 3003);
            var beta = FixtureServer.Deterministic(3 * MiB, seed: 4004);
            server.Add("/files/alpha.bin", alpha);
            server.Add("/files/beta.bin", beta, bytesPerSecond: 128 * 1024);
            var betaUrl = server.Url("/files/beta.bin");

            using var app = AppSession.Launch(data);
            Shots.RunScenario(app, "S04-row-menu-group-filters", () =>
            {
                AppFlows.AddUrl(app, server.Url("/files/alpha.bin"), parts: 1);
                AppFlows.WaitTabCount(app, "Completed", 1);
                AppFlows.AddUrl(app, betaUrl, parts: 1);
                Uia.WaitUntil(() => Uia.TabCount(app.Main, "Downloading") == 1, "beta downloading", TimeSpan.FromSeconds(30));

                // Context menu: the main items exist on the download row.
                // All main items must be present at once in a single menu view.
                AppFlows.OpenRowMenu(app, "beta.bin", "Resume", "Pause", "Open", "Open folder", "Copy link",
                    "Checksum (MD5 / SHA-256)", "Properties", "Remove from list");
                AppFlows.MenuItem(app, "Pause");
                AppFlows.WaitTabCount(app, "Inactive", 1);

                // Copy link: the clipboard must contain that row's URL. If the clipboard is locked
                // (by another app), the app shows a message and we retry after the message is closed.
                CopyLinkUntilClipboardHasUrl(app, "beta.bin", betaUrl);

                // Checksum of the completed alpha row: the dialog must show the correct SHA-256.
                AppFlows.ClickTab(app, "Completed");
                AppFlows.RowMenu(app, "alpha.bin", "Checksum (MD5 / SHA-256)");
                var checksumDlg = Uia.WaitWindow(app.Pid, "Checksum");
                var text = AppFlows.AllText(checksumDlg);
                Assert.Contains(FixtureServer.Sha256Hex(alpha), text);
                AppFlows.CloseDialog(checksumDlg);
                Uia.WaitWindowGone(app.Pid, "Checksum");

                // The "All" tab contains both rows.
                AppFlows.ClickTab(app, "All");
                Assert.NotNull(AppFlows.RowName(app, "alpha.bin"));
                Assert.NotNull(AppFlows.RowName(app, "beta.bin"));

                // Group actions: Start all resumes beta.
                AppFlows.ClickButton(app, "Start all");
                AppFlows.WaitTabCount(app, "Downloading", 1, TimeSpan.FromSeconds(20));
                AppFlows.ClickButton(app, "Pause all");
                AppFlows.WaitTabCount(app, "Inactive", 1, TimeSpan.FromSeconds(20));

                // Search: only matching rows stay visible.
                var search = Uia.FindAll(app.Main, ControlType.Edit).First();
                Uia.SetText(search, "alpha");
                Uia.WaitUntil(() => AppFlows.RowName(app, "beta.bin") is null && AppFlows.RowName(app, "alpha.bin") is not null,
                    "search filter \"alpha\"", TimeSpan.FromSeconds(10));
                Uia.SetText(search, "");
                Uia.WaitUntil(() => AppFlows.RowName(app, "beta.bin") is not null, "beta returns after the search is cleared", TimeSpan.FromSeconds(10));

                // Tab filter: "Completed" is alpha only.
                AppFlows.ClickTab(app, "Completed");
                Uia.WaitUntil(() => AppFlows.RowName(app, "beta.bin") is null && AppFlows.RowName(app, "alpha.bin") is not null,
                    "Completed tab shows alpha only", TimeSpan.FromSeconds(10));
                AppFlows.ClickTab(app, "All");
            });
        }
        finally
        {
            AppSession.TryDeleteDataDir(data);
        }
    }

    /// <summary>
    /// Regression: the clipboard is locked by another app. "Copy link" must not throw an unhandled
    /// exception (the "An unexpected error occurred" box); it must show a message and the app must stay alive.
    /// </summary>
    [Fact]
    public void S04B_CopyLink_ClipboardLocked_ShowsMessage_AppStaysAlive()
    {
        var data = AppSession.NewTempDataDir();
        using var server = FixtureServer.Start();
        try
        {
            server.Add("/files/locked.bin", FixtureServer.Deterministic(64 * 1024, seed: 5005));
            var url = server.Url("/files/locked.bin");

            using var app = AppSession.Launch(data);
            Shots.RunScenario(app, "S04B-copylink-clipboard-locked", () =>
            {
                AppFlows.AddUrl(app, url, parts: 1);
                AppFlows.WaitTabCount(app, "Completed", 1);

                using var locker = ClipboardLock.Hold();
                AppFlows.RowMenu(app, "locked.bin", "Copy link");

                // Wait for the first message box (error or Copy link message) to appear.
                AutomationElement? box = null;
                Uia.WaitUntil(() =>
                {
                    box = Uia.WindowsOfProcess(app.Pid).FirstOrDefault(w => w.Current.ClassName == "#32770");
                    return box is not null;
                }, "message box after Copy link", TimeSpan.FromSeconds(15));

                string title = Uia.SafeName(box!);
                string body = AppFlows.AllText(box!);
                Assert.False(body.Contains("An unexpected error occurred", StringComparison.Ordinal),
                    $"Copy link threw an unhandled exception (title \"{title}\").");
                Assert.Equal("Copy link", title);

                AppFlows.CloseDialog(box!);
                locker.Release();
                Uia.WaitWindowGone(app.Pid, title);
                Assert.NotNull(app.Main);
            });
        }
        finally
        {
            AppSession.TryDeleteDataDir(data);
        }
    }

    /// <summary>Lock the clipboard with OpenClipboard on a separate thread (simulating another app).</summary>
    private sealed class ClipboardLock : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly ManualResetEventSlim _opened = new();
        private Thread? _thread;

        public static ClipboardLock Hold()
        {
            var l = new ClipboardLock();
            l._thread = new Thread(() =>
            {
                if (!NativeClipboard.OpenClipboard(IntPtr.Zero)) { l._opened.Set(); return; }
                l._opened.Set();
                l._release.Wait(TimeSpan.FromSeconds(60));
                NativeClipboard.CloseClipboard();
            });
            l._thread.IsBackground = true;
            l._thread.Start();
            l._opened.Wait(TimeSpan.FromSeconds(5));
            return l;
        }

        public void Release()
        {
            _release.Set();
            _thread?.Join(TimeSpan.FromSeconds(5));
        }

        public void Dispose() => Release();
    }

    private static class NativeClipboard
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool CloseClipboard();
    }

    private static void CopyLinkUntilClipboardHasUrl(AppSession app, string fileName, string expectedUrl)
    {
        // Avoid polling the clipboard too often (every read opens the clipboard and can block SetDataObject).
        // The clipboard can also be locked by another app on the desktop; allow several attempts with a long enough delay.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            AppFlows.RowMenu(app, fileName, "Copy link");
            Thread.Sleep(1200);
            var box = Uia.WindowsOfProcess(app.Pid).FirstOrDefault(w => w.Current.ClassName == "#32770");
            if (box is not null)
            {
                // "Clipboard busy" message (from the fix): close it and retry after a longer delay.
                string title = Uia.SafeName(box);
                AppFlows.CloseDialog(box);
                Uia.WaitWindowGone(app.Pid, title);
                Thread.Sleep(2500);
                continue;
            }
            Assert.Equal(expectedUrl, ClipboardText());
            return;
        }
        Assert.Fail("Copy link failed after 8 attempts; the clipboard is always busy (another app is probably locking it).");
    }

    /// <summary>Read the clipboard text on an STA thread (WinForms APIs require it).</summary>
    internal static string? ClipboardText()
    {
        string? text = null;
        var t = new Thread(() =>
        {
            try { text = System.Windows.Forms.Clipboard.ContainsText() ? System.Windows.Forms.Clipboard.GetText() : null; }
            catch (System.Runtime.InteropServices.ExternalException) { text = null; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return text;
    }
}
