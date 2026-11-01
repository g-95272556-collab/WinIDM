using System.Diagnostics;
using System.Windows.Automation;
using WinIDM.App.E2E.Support;

namespace WinIDM.App.E2E.Tests;

/// <summary>
/// Scenario 8: smoke test over every window that is reachable from the main window. Each window must open,
/// must not show a Win32 error dialog (class "#32770", that is a MessageBox), and must close cleanly with
/// WindowPattern.Close while the main window stays alive.
///
/// One launch covers every window (like the design capture). Every window is a separate step that collects
/// its own failure, so one broken window does not hide the others; the test fails once at the end with the
/// complete list. Windows: the navigation rail (Settings, Torrent, Backup, Passwords, Custom theme editor,
/// About WinIDM), the Add URL dialog, the Scheduler tab and the Browser window.
/// </summary>
[Trait("Category", "E2E")]
public sealed class SmokeAllWindowsTests
{
    /// <summary>Win32 class name of a standard MessageBox.</summary>
    private const string ErrorDialogClassName = "#32770";

    private const string BrowserTitleSuffix = "WinIDM Browser";

    /// <summary>Navigation rail buttons (AutomationProperties.Name) and the window title each one opens.</summary>
    private static readonly (string Button, string Title)[] NavigationRailWindows =
    {
        ("Settings", "Settings"),
        ("Torrent", "Torrent"),
        ("Backup", "Backup and restore"),
        ("Passwords", "Password manager"),
        ("Custom theme editor", "Custom theme editor"),
        ("About WinIDM", "About WinIDM"),
    };

    /// <summary>Generous timeout: window creation is fast, but the desktop is shared with other runs.</summary>
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);

    /// <summary>WebView2 needs longer than a plain WPF window.</summary>
    private static readonly TimeSpan BrowserOpenTimeout = TimeSpan.FromSeconds(40);

    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How long to keep watching for a message box after a window opens (one may appear a moment later).</summary>
    private static readonly TimeSpan ErrorDialogWatch = TimeSpan.FromSeconds(2);

    [Fact]
    public void S08_EveryWindow_OpensWithoutErrorDialog()
    {
        var data = AppSession.NewTempDataDir();
        try
        {
            using var app = AppSession.Launch(data);
            Shots.RunScenario(app, "S08-smoke-all-windows", () =>
            {
                var failures = new List<string>();

                // 1. Windows opened from the main window navigation rail.
                foreach (var (button, title) in NavigationRailWindows)
                    Step(failures, $"\"{title}\" window", () => OpenAndCloseWindow(app, button, title));

                // 2. Add URL: the toolbar button opens the modal "Add download" dialog.
                Step(failures, "\"Add download\" dialog", () => OpenAndCloseWindow(app, "Add URL", "Add download", shotName: "SMOKE-add-download"));

                // 3. Scheduler tab: a ToggleButton named "Scheduler (<count>)" that swaps the download list for its panel.
                Step(failures, "Scheduler tab", () =>
                {
                    var tab = FindTabToggle(app, "Scheduler");
                    Assert.True(tab.TryGetCurrentPattern(TogglePattern.Pattern, out _),
                        "The Scheduler tab is not a ToggleButton (no TogglePattern).");

                    SelectTab(app, "Scheduler");
                    AssertNoErrorDialog(app, "the Scheduler tab");
                    Shots.Save("SMOKE-scheduler");
                    // The scheduler panel replaced the download list (a plain WPF tab switch, no window to close).
                    Assert.NotNull(Uia.Find(app.Main, ControlType.CheckBox, "Enable scheduler", TimeSpan.FromSeconds(10)));
                    SelectTab(app, "All");
                });

                // 4. Browser: "<page title> - WinIDM Browser", so it is looked up by its suffix.
                Step(failures, "Browser window", () =>
                {
                    EnsureAppAlive(app);
                    Uia.Invoke(Uia.Find(app.Main, ControlType.Button, "Browser"));
                    var browser = Uia.WaitWindowEndingWith(app.Pid, BrowserTitleSuffix, BrowserOpenTimeout);
                    AssertNoErrorDialog(app, "the Browser window");
                    Shots.Save("SMOKE-browser");
                    CloseWindow(browser, "Browser window");
                    Uia.WaitUntil(() => Uia.TryFindWindowEndingWith(app.Pid, BrowserTitleSuffix) is null,
                        "browser window closed", CloseTimeout);
                    Assert.Equal(AppSession.MainTitle, Uia.SafeName(app.Main));
                });

                // 5. Nothing may be left behind: no message box and no dead process after all windows were closed.
                Step(failures, "main window after all windows", () =>
                {
                    EnsureAppAlive(app);
                    AssertNoErrorDialog(app, "the end of the run");
                    Assert.Equal(AppSession.MainTitle, Uia.SafeName(app.Main));
                });

                Assert.Empty(failures);
            });
        }
        finally
        {
            AppSession.TryDeleteDataDir(data);
        }
    }

    /// <summary>Open a window from a main-window button, check for an error dialog, close it and wait until it is gone.</summary>
    private static void OpenAndCloseWindow(AppSession app, string buttonName, string windowTitle, string? shotName = null)
    {
        EnsureAppAlive(app);
        Uia.Invoke(Uia.Find(app.Main, ControlType.Button, buttonName));
        var window = Uia.WaitWindow(app.Pid, windowTitle, OpenTimeout);

        AssertNoErrorDialog(app, $"the \"{windowTitle}\" window");
        Shots.Save(shotName ?? "SMOKE-" + FileNameSafe(windowTitle));

        CloseWindow(window, windowTitle);
        Uia.WaitUntil(() => Uia.TryFindWindow(app.Pid, windowTitle) is null,
            $"window \"{windowTitle}\" closed", CloseTimeout);

        // The main window must survive every tool window.
        Assert.Equal(AppSession.MainTitle, Uia.SafeName(app.Main));
    }

    /// <summary>Close a window through WindowPattern (the pattern the whole E2E suite uses).</summary>
    private static void CloseWindow(AutomationElement window, string what)
    {
        Assert.True(window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern),
            $"The {what} does not support WindowPattern.Close.");
        ((WindowPattern)pattern!).Close();
    }

    /// <summary>
    /// Assert that no Win32 error dialog (class "#32770") is open for the process. The process windows are
    /// watched for a short period, because a message box is not always created in the same instant as the
    /// window that caused it.
    /// </summary>
    private static void AssertNoErrorDialog(AppSession app, string context)
    {
        var deadline = DateTime.UtcNow + ErrorDialogWatch;
        while (true)
        {
            var errorBox = Uia.WindowsOfProcess(app.Pid).FirstOrDefault(IsErrorDialog);
            // Build the detail only when a box exists: AllText needs a live element.
            string detail = errorBox is null
                ? ""
                : $" Window title: \"{Uia.SafeName(errorBox)}\"; text: {AppFlows.AllText(errorBox).Replace("\n", " | ")}";
            Assert.True(errorBox is null,
                $"A Win32 error dialog (class \"{ErrorDialogClassName}\", a MessageBox) appeared for {context}.{detail}");
            if (DateTime.UtcNow >= deadline) return;
            Thread.Sleep(200);
        }
    }

    private static bool IsErrorDialog(AutomationElement window)
    {
        try
        {
            return string.Equals(window.Current.ClassName, ErrorDialogClassName, StringComparison.Ordinal);
        }
        catch (ElementNotAvailableException)
        {
            // The window was closed between the enumeration and the property read.
            return false;
        }
    }

    /// <summary>
    /// The filter tabs are ToggleButtons, which WPF exposes with the Button control type (as everywhere else
    /// in this suite: <see cref="AppFlows.TabButton"/>); the TogglePattern is what proves it is a ToggleButton.
    /// </summary>
    private static AutomationElement FindTabToggle(AppSession app, string title) =>
        Uia.Wait<AutomationElement>(() => Uia.FindAll(app.Main, ControlType.Button)
                .FirstOrDefault(e => Uia.SafeName(e).StartsWith(title + " (", StringComparison.Ordinal)),
            $"tab toggle \"{title} (\"");

    /// <summary>Select a filter tab and verify it through TogglePattern.On (retries if the click was lost).</summary>
    private static void SelectTab(AppSession app, string title)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Uia.Invoke(FindTabToggle(app, title));
            try
            {
                Uia.WaitUntil(() => IsToggledOn(FindTabToggle(app, title)),
                    $"tab \"{title}\" selected", TimeSpan.FromSeconds(4));
                return;
            }
            catch (TimeoutException)
            {
                // Another attempt: the click may have landed while the window was activating.
            }
        }
        throw new TimeoutException($"The tab \"{title}\" could not be selected after 3 attempts.");
    }

    private static bool IsToggledOn(AutomationElement e) =>
        e.TryGetCurrentPattern(TogglePattern.Pattern, out var p) && ((TogglePattern)p).Current.ToggleState == ToggleState.On;

    /// <summary>Fail fast instead of waiting 30 s for every remaining window when the app has crashed.</summary>
    private static void EnsureAppAlive(AppSession app)
    {
        try
        {
            using var process = Process.GetProcessById(app.Pid);
            if (process.HasExited)
                throw new InvalidOperationException($"WinIDM.App (pid {app.Pid}) exited unexpectedly.");
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"WinIDM.App (pid {app.Pid}) exited unexpectedly.");
        }
    }

    /// <summary>Run one window step; a failure is recorded so the remaining windows are still exercised.</summary>
    private static void Step(List<string> failures, string label, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            failures.Add($"{label}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string FileNameSafe(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c))
            .Replace(' ', '-')
            .ToLowerInvariant();
}
