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

    private const int Attempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>Must be called on the UI (STA) thread. Returns false when the clipboard stayed busy.</summary>
    public static async Task<bool> TryCopyLinkAsync(string text)
    {
        for (int attempt = 1; ; attempt++)
        {
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
                if (attempt >= Attempts)
                {
                    AppLog.Error("Clipboard write", ex);
                    return false;
                }
            }
            await Task.Delay(RetryDelay);
        }
    }
}
