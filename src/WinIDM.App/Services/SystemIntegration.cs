using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinIDM.App.Services;

/// <summary>Local crash log only. URLs are scrubbed before writing (OTH-06). Nothing is sent anywhere.</summary>
public static class AppLog
{
    private static string? _dir;
    private static readonly object Gate = new();

    public static void Init(string logDir)
    {
        _dir = logDir;
        try { Directory.CreateDirectory(logDir); } catch (IOException) { }
    }

    public static void Error(string context, Exception ex)
    {
        Write("ERROR", $"{context}: {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>Whether informational (INFO) entries are captured. Off by default: errors are always logged.</summary>
    public static Func<bool> CaptureEnabled { get; set; } = () => false;

    /// <summary>Information is written only when "Capture log" or "Download diagnostics" is on.</summary>
    public static void Info(string message)
    {
        if (CaptureEnabled()) Write("INFO", message);
    }

    private static void Write(string level, string message)
    {
        if (_dir is null) return;
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {DiagnosticsRedactor.Text(message)}{Environment.NewLine}";
        try
        {
            lock (Gate) File.AppendAllText(Path.Combine(_dir, $"app-{DateTime.Now:yyyyMMdd}.log"), line);
        }
        catch (IOException) { }
    }
}

/// <summary>Registration for the current user (HKCU only, no administrator rights needed).</summary>
public static class SystemIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "WinIDM";
    private const string TorrentProgId = "WinIDM.Torrent";

    private static string ExePath => Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "WinIDM.exe";

    /// <summary>Start with Windows (written to HKCU\...\Run). The --minimized argument starts minimized to the tray.</summary>
    public static void SetStartWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{ExePath}\" --minimized");
        else if (key.GetValue(RunValue) is not null) key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    /// <summary>
    /// Registers the magnet:, 1dmdownload: and idmdownload: protocols and the .torrent file association (HKCU).
    /// Unregistering deletes only the keys that point to this application.
    /// </summary>
    public static void SetProtocolRegistration(bool enabled)
    {
        var exe = ExePath;
        var command = $"\"{exe}\" \"%1\"";
        foreach (var (scheme, desc) in new[] { ("magnet", "URL:Magnet link"), ("1dmdownload", "URL:WinIDM Download"), ("idmdownload", "URL:WinIDM Download") })
        {
            if (enabled)
            {
                using var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}");
                root.SetValue(null, desc);
                root.SetValue("URL Protocol", "");
                using var cmd = root.CreateSubKey(@"shell\open\command");
                cmd.SetValue(null, command);
            }
            else
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{scheme}", throwOnMissingSubKey: false);
            }
        }

        if (enabled)
        {
            using (var ext = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.torrent"))
                ext.SetValue(null, TorrentProgId);
            using var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{TorrentProgId}");
            prog.SetValue(null, "Torrent file (WinIDM)");
            using var cmd = prog.CreateSubKey(@"shell\open\command");
            cmd.SetValue(null, command);
        }
        else
        {
            using var ext = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.torrent", writable: true);
            if (ext?.GetValue(null) as string == TorrentProgId) ext.SetValue(null, "");
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{TorrentProgId}", throwOnMissingSubKey: false);
        }
    }

    public static bool IsProtocolRegistered()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\1dmdownload\shell\open\command");
        return k?.GetValue(null) is string s && s.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// System sleep control while downloads are active (SetThreadExecutionState).
/// Called from the UI thread only.
/// </summary>
public static class PowerState
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    public static void Update(bool busy, bool keepSystemAwake, bool keepDisplayOn)
    {
        uint flags = EsContinuous;
        if (busy && keepSystemAwake) flags |= EsSystemRequired;
        if (busy && keepDisplayOn) flags |= EsDisplayRequired;
        try { SetThreadExecutionState(flags); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}

/// <summary>
/// Status-bar view of the engine's network policy. The engine enforces Wi-Fi only and pause-on-metered itself;
/// this only describes the same <see cref="WinIDM.Engine.INetworkPolicy"/> state, so the label and the policy never disagree.
/// </summary>
public sealed record NetworkStatus(bool Online, bool WifiOrEthernet, bool Metered)
{
    public static NetworkStatus From(WinIDM.Engine.INetworkPolicy policy) =>
        new(policy.IsOnline, policy.IsWifiOrEthernet, policy.IsMetered);

    public string Label => !Online ? "No connection"
        : Metered ? (WifiOrEthernet ? "Metered connection" : "Mobile data (metered)")
        : WifiOrEthernet ? "Wi-Fi / wired" : "Mobile data";

    /// <summary>True when "Wi-Fi only" is on and the engine is holding downloads back for it.</summary>
    public bool BlocksWifiOnly(bool wifiOnly) => wifiOnly && Online && !WifiOrEthernet;
}
