using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;

namespace WinIDM.App.E2E.Support;

/// <summary>
/// A single WinIDM.App process started by a test with a temporary --data-dir. The process is always terminated
/// in <see cref="Dispose"/> (including its process tree, that is, the child WebView2 processes).
/// </summary>
internal sealed class AppSession : IDisposable
{
    public const string MainTitle = "WinIDM";

    private readonly Process _proc;
    private bool _disposed;

    private AppSession(Process proc, string dataDir, string downloadDir)
    {
        _proc = proc;
        DataDir = dataDir;
        DownloadDir = downloadDir;
    }

    public string DataDir { get; }

    /// <summary>Default download folder (set through settings.json before launch).</summary>
    public string DownloadDir { get; }

    public int Pid => _proc.Id;

    /// <summary>Main window (title "WinIDM").</summary>
    public AutomationElement Main => Uia.WaitWindow(Pid, MainTitle, TimeSpan.FromSeconds(30));

    /// <summary>
    /// Launch the exe with a unique temporary data folder. <paramref name="seed"/> is written to settings.json
    /// before launch (merged with the existing file, if any). Safe defaults: no Windows registration,
    /// no tray, no sound, no old link list.
    /// </summary>
    public static AppSession Launch(
        string dataDir,
        IDictionary<string, string>? seed = null,
        IDictionary<string, string>? env = null,
        params string[] extraArgs)
    {
        var exe = RepoPaths.AppExe;
        if (!File.Exists(exe)) throw new FileNotFoundException("Target exe not found. Run build\\publish.ps1 or set WINIDM_EXE.", exe);

        // Single instance: if WinIDM is already running, the arguments are sent to that instance and the test will not start its own process.
        if (Process.GetProcessesByName("WinIDM.App").Length > 0)
            throw new InvalidOperationException("WinIDM.App is already running. Close it first so the test does not attach to the user's instance.");

        Directory.CreateDirectory(dataDir);
        var downloads = Path.Combine(dataDir, "downloads");
        Directory.CreateDirectory(downloads);

        var merged = ReadSettings(dataDir);
        var defaults = new Dictionary<string, string>
        {
            ["app.default_folder"] = downloads,
            ["app.minimize_to_tray"] = "false",
            ["app.start_with_windows"] = "false",
            ["app.register_protocols"] = "false",
            ["idm_pref_sound_options"] = "false",
            ["idm_pref_smart_download"] = "false",
        };
        foreach (var kv in defaults) if (!merged.ContainsKey(kv.Key)) merged[kv.Key] = kv.Value;
        if (seed is not null) foreach (var kv in seed) merged[kv.Key] = kv.Value;
        File.WriteAllText(Path.Combine(dataDir, "settings.json"), JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true }));

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        psi.ArgumentList.Add("--data-dir");
        psi.ArgumentList.Add(dataDir);
        foreach (var a in extraArgs) psi.ArgumentList.Add(a);
        if (env is not null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start WinIDM.App.exe.");
        var session = new AppSession(proc, dataDir, downloads);
        try
        {
            Uia.WaitWindow(proc.Id, MainTitle, TimeSpan.FromSeconds(40));
        }
        catch
        {
            session.Dispose();
            throw;
        }
        return session;
    }

    /// <summary>Close gracefully (WindowPattern.Close), then force-kill the process tree if it has not exited.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_proc.HasExited)
            {
                try
                {
                    var main = Uia.TryFindWindow(_proc.Id, MainTitle);
                    if (main is not null && main.TryGetCurrentPattern(WindowPattern.Pattern, out var wp))
                        ((WindowPattern)wp).Close();
                }
                catch (Exception)
                {
                    // A graceful close is best effort; force-kill below.
                }
                if (!_proc.WaitForExit(10_000))
                {
                    _proc.Kill(entireProcessTree: true);
                    _proc.WaitForExit(10_000);
                }
            }
        }
        finally
        {
            _proc.Dispose();
        }
    }

    /// <summary>Relaunch the process with the same data folder (for settings persistence tests).</summary>
    public static AppSession Restart(AppSession previous)
    {
        var dataDir = previous.DataDir;
        previous.Dispose();
        return Launch(dataDir);
    }

    private static Dictionary<string, string> ReadSettings(string dataDir)
    {
        var path = Path.Combine(dataDir, "settings.json");
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
               ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Unique temporary data folder under %TEMP% (does not touch %LOCALAPPDATA%\WinIDM).</summary>
    public static string NewTempDataDir() =>
        Path.Combine(Path.GetTempPath(), "winidm-e2e-" + Guid.NewGuid().ToString("N"));

    /// <summary>Delete the temporary folder (errors ignored: files may still be briefly locked).</summary>
    public static void TryDeleteDataDir(string dataDir)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
                return;
            }
            catch (IOException) { Thread.Sleep(400); }
            catch (UnauthorizedAccessException) { Thread.Sleep(400); }
        }
    }
}
