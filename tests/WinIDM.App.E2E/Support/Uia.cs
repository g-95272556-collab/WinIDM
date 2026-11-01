using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Forms;

namespace WinIDM.App.E2E.Support;

/// <summary>UI Automation helpers: find, wait, click, type text and read values (System.Windows.Automation, no extra NuGet packages).</summary>
internal static class Uia
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);

    private const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010;
    private const byte VkMenu = 0x12;
    private const uint KeyEventfKeyUp = 0x0002;

    // ------------------------------------------------------------------
    // Find
    // ------------------------------------------------------------------

    /// <summary>All top-level windows of a given process.</summary>
    /// <summary>
    /// All windows of a process: top-level windows and owned windows (dialogs) that WPF
    /// exposes as descendants of their owner window in the UIA tree.
    /// </summary>
    public static List<AutomationElement> WindowsOfProcess(int pid)
    {
        var cond = new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, pid),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
        var result = new List<AutomationElement>();
        foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children, cond))
        {
            result.Add(top);
            result.AddRange(FindAll(top, ControlType.Window, null));
        }
        return result;
    }

    public static AutomationElement? TryFindWindow(int pid, string title) =>
        WindowsOfProcess(pid).FirstOrDefault(w => string.Equals(SafeName(w), title, StringComparison.Ordinal));

    public static AutomationElement WaitWindow(int pid, string title, TimeSpan? timeout = null)
    {
        try
        {
            return Wait(() => TryFindWindow(pid, title), $"window \"{title}\"", timeout);
        }
        catch (TimeoutException)
        {
            // Diagnostics: list the windows that actually exist for this process.
            var names = WindowsOfProcess(pid).Select(SafeName).ToList();
            var desktop = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition)
                .Cast<AutomationElement>()
                .Select(w => $"{SafeName(w)}@{w.Current.ProcessId}")
                .Where(s => !s.StartsWith("@", StringComparison.Ordinal))
                .Take(60);
            throw new TimeoutException(
                $"Timed out waiting for: window \"{title}\" (pid {pid}). Process windows: [{string.Join(" | ", names)}]. " +
                $"Desktop: [{string.Join(" | ", desktop)}]");
        }
    }

    /// <summary>Window whose title ends with <paramref name="suffix"/> (for example: "<page title> - WinIDM Browser").</summary>
    public static AutomationElement? TryFindWindowEndingWith(int pid, string suffix) =>
        WindowsOfProcess(pid).FirstOrDefault(w => SafeName(w).EndsWith(suffix, StringComparison.Ordinal));

    public static AutomationElement WaitWindowEndingWith(int pid, string suffix, TimeSpan? timeout = null) =>
        Wait(() => TryFindWindowEndingWith(pid, suffix), $"window ending with \"{suffix}\"", timeout);

    public static void WaitWindowGone(int pid, string title, TimeSpan? timeout = null) =>
        WaitUntil(() => TryFindWindow(pid, title) is null, $"window \"{title}\" closed", timeout);

    /// <summary>
    /// Search in ALL top-level windows of the process (including context menus/popups that are not of type Window).
    /// </summary>
    public static AutomationElement? TryFindInProcess(int pid, ControlType type, string name)
    {
        var procCond = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
        foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children, procCond))
        {
            var hit = TryFind(top, type, name);
            if (hit is not null) return hit;
        }
        return null;
    }

    public static AutomationElement? TryFind(AutomationElement root, ControlType? type, string? name, TreeScope scope = TreeScope.Descendants)
    {
        try
        {
            return root.FindFirst(scope, Cond(type, name));
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    public static AutomationElement Find(AutomationElement root, ControlType? type, string? name, TimeSpan? timeout = null) =>
        Wait(() => TryFind(root, type, name), $"{type?.ProgrammaticName ?? "*"} \"{name}\"", timeout);

    public static List<AutomationElement> FindAll(AutomationElement root, ControlType? type, string? name = null, TreeScope scope = TreeScope.Descendants)
    {
        try
        {
            return root.FindAll(scope, Cond(type, name)).Cast<AutomationElement>().ToList();
        }
        catch (ElementNotAvailableException)
        {
            return new List<AutomationElement>();
        }
    }

    /// <summary>Filter tab button, e.g. "Completed" -> ToggleButton named "Completed (n)".</summary>
    public static AutomationElement TabButton(AutomationElement main, string title) =>
        Wait(() => FindAll(main, ControlType.Button)
                .FirstOrDefault(b => SafeName(b).StartsWith(title + " (", StringComparison.Ordinal)),
             $"tab \"{title}\"");

    /// <summary>Count in a tab, e.g. "Completed (2)" -> 2.</summary>
    public static int TabCount(AutomationElement main, string title)
    {
        var name = SafeName(TabButton(main, title));
        int open = name.LastIndexOf('(');
        int close = name.LastIndexOf(')');
        return int.Parse(name.AsSpan(open + 1, close - open - 1));
    }

    /// <summary>Nearest Edit field to an element (closest ancestor that contains an Edit), e.g. the TextBox next to the "Save to..." button.</summary>
    public static AutomationElement FindEditNear(AutomationElement el)
    {
        var walker = TreeWalker.ControlViewWalker;
        var center = el.Current.BoundingRectangle;
        var p = walker.GetParent(el);
        for (int depth = 0; p is not null && depth < 4; depth++, p = walker.GetParent(p))
        {
            // In this subtree, pick the Edit closest vertically (on the same row) to the element.
            var edits = FindAll(p, ControlType.Edit, null)
                .Where(e => !e.Current.BoundingRectangle.IsEmpty)
                .OrderBy(e => Math.Abs(e.Current.BoundingRectangle.Top - center.Top))
                .ToList();
            if (edits.Count > 0) return edits[0];
        }
        throw new InvalidOperationException($"Edit field near \"{SafeName(el)}\" not found.");
    }

    /// <summary>Text element (TextBlock) with exact content, inside the main window.</summary>
    public static AutomationElement? TryFindText(AutomationElement root, string text) =>
        TryFind(root, ControlType.Text, text);

    // ------------------------------------------------------------------
    // Actions
    // ------------------------------------------------------------------

    public static void Invoke(AutomationElement e)
    {
        if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var p))
        {
            ((InvokePattern)p).Invoke();
            return;
        }
        Click(e);
    }

    public static void SetText(AutomationElement e, string text)
    {
        if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var p))
        {
            ((ValuePattern)p).SetValue(text);
            return;
        }
        throw new InvalidOperationException("The element does not support ValuePattern.");
    }

    public static string Value(AutomationElement e) =>
        e.TryGetCurrentPattern(ValuePattern.Pattern, out var p) ? ((ValuePattern)p).Current.Value : SafeName(e);

    public static string SafeName(AutomationElement e)
    {
        try { return e.Current.Name; }
        catch (ElementNotAvailableException) { return ""; }
    }

    /// <summary>
    /// Left-click the center of an element. Safety: the parent window is activated first, and the click point
    /// must land on an element owned by the same process (UIA FromPoint). Otherwise the click is CANCELED so
    /// other applications on the user's desktop are not clicked.
    /// </summary>
    public static void Click(AutomationElement e)
    {
        var (x, y) = PrepareClick(e);
        SetCursorPos(x, y);
        mouse_event(LeftDown, 0, 0, 0, 0);
        mouse_event(LeftUp, 0, 0, 0, 0);
    }

    public static void RightClick(AutomationElement e)
    {
        var (x, y) = PrepareClick(e);
        SetCursorPos(x, y);
        mouse_event(RightDown, 0, 0, 0, 0);
        mouse_event(RightUp, 0, 0, 0, 0);
    }

    private static (int X, int Y) PrepareClick(AutomationElement e)
    {
        var (x, y) = Center(e);
        int pid = e.Current.ProcessId;
        ActivateWindowOf(e);

        // Verification: at the click point, the top element must belong to the test process.
        var hit = AutomationElement.FromPoint(new System.Windows.Point(x, y));
        if (hit.Current.ProcessId != pid)
            throw new InvalidOperationException(
                $"Click canceled: at ({x},{y}) the top element belongs to process {hit.Current.ProcessId}, not {pid} (\"{SafeName(e)}\"). Make sure the desktop is not in use during the test.");
        return (x, y);
    }

    private static (int X, int Y) Center(AutomationElement e)
    {
        var r = e.Current.BoundingRectangle;
        if (r.IsEmpty || double.IsInfinity(r.Left)) throw new InvalidOperationException($"Element \"{SafeName(e)}\" has no position.");
        return ((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
    }

    /// <summary>
    /// Best effort to activate the window above an element. Windows may refuse focus (shared desktop);
    /// click safety is guaranteed by the UIA hit-test in <see cref="PrepareClick"/>, not by focus.
    /// </summary>
    private static void ActivateWindowOf(AutomationElement e)
    {
        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? win = e;
        while (win is not null && win.Current.ControlType != ControlType.Window)
            win = walker.GetParent(win);
        var hwnd = win?.Current.NativeWindowHandle ?? 0;
        if (hwnd == 0) return;

        // Pressing and releasing Alt allows SetForegroundWindow from a background process.
        keybd_event(VkMenu, 0, 0, 0);
        keybd_event(VkMenu, 0, KeyEventfKeyUp, 0);
        SetForegroundWindow(hwnd);
        Thread.Sleep(80);
    }

    /// <summary>Is this process in the foreground? Keys are sent only when it is (to avoid typing into another app).</summary>
    public static bool IsForeground(int pid)
    {
        var fg = GetForegroundWindow();
        return fg != 0 && GetWindowThreadProcessIdOf(fg) == pid;
    }

    /// <summary>
    /// Dismiss open menus/popups: ESC if this process is in the foreground; otherwise click the main window title bar.
    /// </summary>
    public static void DismissPopups(AutomationElement main)
    {
        if (IsForeground(main.Current.ProcessId))
        {
            SendKeys.SendWait("{ESC}");
            return;
        }
        var r = main.Current.BoundingRectangle;
        if (r.IsEmpty) return;
        int x = (int)(r.Left + r.Width / 2), y = (int)(r.Top + 14);
        var hit = AutomationElement.FromPoint(new System.Windows.Point(x, y));
        if (hit.Current.ProcessId != main.Current.ProcessId) return; // this point does not belong to WinIDM: do not click
        SetCursorPos(x, y);
        mouse_event(LeftDown, 0, 0, 0, 0);
        mouse_event(LeftUp, 0, 0, 0, 0);
    }

    private static int GetWindowThreadProcessIdOf(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    private static Condition Cond(ControlType? type, string? name)
    {
        var conds = new List<Condition>();
        if (type is not null) conds.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, type));
        if (name is not null) conds.Add(new PropertyCondition(AutomationElement.NameProperty, name));
        return conds.Count switch
        {
            0 => Condition.TrueCondition,
            1 => conds[0],
            _ => new AndCondition(conds.ToArray()),
        };
    }

    // ------------------------------------------------------------------
    // Wait
    // ------------------------------------------------------------------

    public static T Wait<T>(Func<T?> probe, string what, TimeSpan? timeout = null) where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            var v = probe();
            if (v is not null) return v;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            Thread.Sleep(150);
        }
    }

    public static void WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Condition not met within the timeout: {what}");
            Thread.Sleep(150);
        }
    }

    // ------------------------------------------------------------------
    // Diagnostics
    // ------------------------------------------------------------------

    /// <summary>Write the UIA tree (type, name, value) to a text file for debugging.</summary>
    public static void Dump(AutomationElement root, string path, int maxDepth = 14)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var sw = new StreamWriter(path);
        DumpNode(root, sw, 0, maxDepth);
    }

    private static void DumpNode(AutomationElement e, TextWriter w, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;
        string value = e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) ? ((ValuePattern)vp).Current.Value : "";
        w.WriteLine($"{new string(' ', depth * 2)}[{e.Current.ControlType.ProgrammaticName}] name=\"{SafeName(e)}\" value=\"{value}\"");
        foreach (AutomationElement c in e.FindAll(TreeScope.Children, Condition.TrueCondition))
            DumpNode(c, w, depth + 1, maxDepth);
    }
}
