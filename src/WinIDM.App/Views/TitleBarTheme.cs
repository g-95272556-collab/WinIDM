using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WinIDM.App.Services;

namespace WinIDM.App.Views;

/// <summary>
/// Paints the native window frame (title bar and border) dark when the app theme is dark, so the system chrome
/// matches the rest of the window. Best effort: on Windows versions without the API the window keeps its
/// default frame.
/// </summary>
internal static class TitleBarTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            int dark = ThemeService.IsDark ? 1 : 0;
            // Attribute 20 is current; builds before 20H1 only honor 19. Older builds report success for the
            // unknown attribute, so both are attempted rather than branching on the return code.
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Older Windows builds do not expose the API; the default frame is fine.
        }
        catch (EntryPointNotFoundException)
        {
            // Same as above.
        }
    }
}
