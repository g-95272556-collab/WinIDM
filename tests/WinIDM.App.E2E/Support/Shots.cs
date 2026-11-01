using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Forms;

namespace WinIDM.App.E2E.Support;

/// <summary>
/// PNG screenshots into tests\WinIDM.App.E2E\artifacts. Used for every passing (PASS_) and failing (FAIL_) scenario.
/// On failure, the app logs (logs folder) are copied too, so the cause can be traced even after the data folder is deleted.
/// </summary>
internal static class Shots
{
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    public static string Save(string name)
    {
        Directory.CreateDirectory(RepoPaths.ArtifactsDir);
        var path = Path.Combine(RepoPaths.ArtifactsDir, Sanitize(name) + ".png");
        try
        {
            var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
            using var bmp = new Bitmap(bounds.Width, bounds.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }
            bmp.Save(path, ImageFormat.Png);
        }
        catch (Exception ex)
        {
            // Screenshots are a diagnostic aid; their failure must not change the test result.
            File.WriteAllText(path + ".error.txt", ex.ToString());
        }
        return path;
    }

    /// <summary>
    /// Capture one window (not the whole screen) with <c>PrintWindow</c> + <c>PW_RENDERFULLCONTENT</c>, so the shot is
    /// correct even when another application overlaps or Windows refuses the foreground change. Falls back to the
    /// window rectangle on the screen when the window cannot be rendered offscreen.
    /// </summary>
    public static string SaveWindow(AutomationElement window, string name)
    {
        Directory.CreateDirectory(RepoPaths.ArtifactsDir);
        var path = Path.Combine(RepoPaths.ArtifactsDir, Sanitize(name) + ".png");
        try
        {
            var handle = new IntPtr(window.Current.NativeWindowHandle);
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return Save(name);

            int width = Math.Max(1, rect.Right - rect.Left);
            int height = Math.Max(1, rect.Bottom - rect.Top);
            using var bmp = new Bitmap(width, height);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                bool ok;
                try
                {
                    ok = PrintWindow(handle, hdc, 2);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
                if (!ok) g.CopyFromScreen(new Point(rect.Left, rect.Top), Point.Empty, new Size(width, height));
            }
            bmp.Save(path, ImageFormat.Png);
        }
        catch (Exception ex)
        {
            File.WriteAllText(path + ".error.txt", ex.ToString());
        }
        return path;
    }

    /// <summary>Run a scenario: capture PASS_ or FAIL_ (with app logs) when it finishes, then rethrow the error.</summary>
    public static void RunScenario(AppSession? app, string name, Action body)
    {
        try
        {
            body();
        }
        catch
        {
            Save("FAIL_" + name);
            if (app is not null) CopyLogs(app, name);
            throw;
        }
        Save("PASS_" + name);
    }

    private static void CopyLogs(AppSession app, string name)
    {
        try
        {
            var src = Path.Combine(app.DataDir, "logs");
            if (!Directory.Exists(src)) return;
            var dst = Path.Combine(RepoPaths.ArtifactsDir, "FAIL_" + Sanitize(name) + "-logs");
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.GetFiles(src))
            {
                // Shared read: the app may still have the log file open.
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = File.Create(Path.Combine(dst, Path.GetFileName(file)));
                input.CopyTo(output);
            }
        }
        catch (IOException)
        {
            // Diagnostics only.
        }
    }

    private static string Sanitize(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
