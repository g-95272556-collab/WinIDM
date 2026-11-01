using System.Runtime.InteropServices;
using System.Windows;

namespace WinIDM.App.Services;

/// <summary>
/// Copies links the app itself produced. The CPFROM1DM marker tells <see cref="ClipboardMonitor"/> not to offer the
/// link as a new download. The clipboard can be held open briefly by the monitor or another app, so a failed write
/// is retried after a short await (never a sleep: the UI thread stays responsive).
/// </summary>
public static class ClipboardWriter
{
    public const string FromAppFormat = "CPFROM1DM";

    private const int Attempts = 8;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>Must be called on the UI (STA) thread. Returns false when the clipboard stayed busy.</summary>
    public static async Task<bool> TryCopyLinkAsync(string text)
    {
        for (int attempt = 1; ; attempt++)
        {
            var sequence = GetClipboardSequenceNumber();
            try
            {
                var data = new DataObject();
                data.SetText(text);
                data.SetData(FromAppFormat, true);
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (ExternalException ex)
            {
                // SetDataObject(copy: true) can place the data and then fail to flush it because another reader
                // (the clipboard monitor, another app) opened the clipboard at once. The copy still worked then:
                // the clipboard changed and this process owns it. Neither check opens the clipboard.
                if (GetClipboardSequenceNumber() != sequence && OwnedByThisProcess()) return true;
                if (attempt >= Attempts)
                {
                    AppLog.Error("Clipboard write", ex);
                    return false;
                }
            }
            await Task.Delay(RetryDelay);
        }
    }

    private static bool OwnedByThisProcess()
    {
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) return false;
        GetWindowThreadProcessId(owner, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
