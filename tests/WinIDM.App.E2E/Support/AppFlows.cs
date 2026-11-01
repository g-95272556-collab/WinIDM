using System.Windows.Automation;

namespace WinIDM.App.E2E.Support;

/// <summary>Shared UI flows across scenarios (add URL, tabs, context menu, settings).</summary>
internal static class AppFlows
{
    /// <summary>Tab labels in main-window order.</summary>
    public static readonly string[] TabTitles =
    {
        "All", "Downloading", "Started", "Active", "Inactive", "Unfinished",
        "Completed", "Error", "Missing files", "Scheduled", "Scheduler",
    };

    /// <summary>
    /// Add a URL through the real "Add URL" dialog. Edit field order in the dialog (per the UIA tree):
    /// 0 URL, 1 File name, 2 Folder, 3 Segments, 4 Username, 5 Password (PasswordBox), 6 Referer.
    /// </summary>
    public static void AddUrl(AppSession app, string url, int parts = 0, string? fileName = null)
    {
        Uia.Invoke(Uia.Find(app.Main, ControlType.Button, "Add URL"));
        var dlg = Uia.WaitWindow(app.Pid, "Add download");
        var edits = Uia.FindAll(dlg, ControlType.Edit);
        if (edits.Count < 4) throw new InvalidOperationException($"The Add URL dialog has {edits.Count} Edit fields (expected at least 4).");

        Uia.SetText(edits[0], url);
        if (fileName is not null) Uia.SetText(edits[1], fileName);
        if (parts > 0) Uia.SetText(edits[3], parts.ToString());

        Uia.Invoke(Uia.Find(dlg, ControlType.Button, "Add"));
        Uia.WaitWindowGone(app.Pid, "Add download");
    }

    /// <summary>
    /// Select a filter tab. Verified through TogglePattern (On); retries if the click does not change the state.
    /// </summary>
    public static void ClickTab(AppSession app, string title)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var tab = Uia.TabButton(app.Main, title);
            Uia.Invoke(tab);
            try
            {
                Uia.WaitUntil(() => IsToggledOn(Uia.TabButton(app.Main, title)), $"tab \"{title}\" selected", TimeSpan.FromSeconds(3));
                return;
            }
            catch (TimeoutException)
            {
                // try again
            }
        }
        throw new TimeoutException($"Tab \"{title}\" could not be selected after 3 attempts.");
    }

    private static bool IsToggledOn(AutomationElement e) =>
        e.TryGetCurrentPattern(TogglePattern.Pattern, out var p) && ((TogglePattern)p).Current.ToggleState == ToggleState.On;

    /// <summary>
    /// Make sure the row (the ListViewItem parent of the name TextBlock) is selected before the context menu opens.
    /// The ListView context menu acts on the selected item, so the selection must be verified.
    /// </summary>
    private static void EnsureRowSelected(AutomationElement nameText)
    {
        var walker = System.Windows.Automation.TreeWalker.ControlViewWalker;
        AutomationElement? item = nameText;
        // WPF GridView exposes the ListViewItem as a DataItem (or ListItem, depending on the template).
        while (item is not null && item.Current.ControlType != ControlType.DataItem && item.Current.ControlType != ControlType.ListItem)
            item = walker.GetParent(item);
        if (item is null) throw new InvalidOperationException("Parent ListView row not found.");

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p) && ((SelectionItemPattern)p).Current.IsSelected)
                return;
            Uia.Click(nameText);
            Thread.Sleep(250);
        }
        throw new InvalidOperationException($"Row \"{Uia.SafeName(nameText)}\" could not be selected.");
    }

    public static void WaitTabCount(AppSession app, string title, int count, TimeSpan? timeout = null)
    {
        try
        {
            Uia.WaitUntil(() => Uia.TabCount(app.Main, title) == count, $"tab \"{title}\" = {count}", timeout ?? TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"tab \"{title}\" did not reach {count}. Current tabs: [{TabSnapshot(app)}]");
        }
    }

    /// <summary>Counts of all tabs (diagnostics).</summary>
    public static string TabSnapshot(AppSession app) =>
        string.Join(" | ", Uia.FindAll(app.Main, ControlType.Button)
            .Select(Uia.SafeName)
            .Where(n => TabTitles.Any(t => n.StartsWith(t + " (", StringComparison.Ordinal))));

    public static void WaitTabAtLeast(AppSession app, string title, int count, TimeSpan? timeout = null) =>
        Uia.WaitUntil(() => Uia.TabCount(app.Main, title) >= count,
            $"tab \"{title}\" at least {count}", timeout ?? TimeSpan.FromSeconds(60));

    /// <summary>Click a toolbar button or another main-window button by name.</summary>
    public static void ClickButton(AppSession app, string name) => Uia.Invoke(Uia.Find(app.Main, ControlType.Button, name));

    /// <summary>File name text in the list (TextBlock). Null when absent from the current tab/filter.</summary>
    public static AutomationElement? RowName(AppSession app, string fileName) => Uia.TryFind(app.Main, ControlType.Text, fileName);

    /// <summary>Select a row (left-click its name), then open the context menu and click the item.</summary>
    public static void RowMenu(AppSession app, string fileName, string menuItem)
    {
        OpenRowMenu(app, fileName, menuItem);
        MenuItem(app, menuItem);
    }

    /// <summary>
    /// Select a row and open its context menu. Succeeds only if ALL <paramref name="requiredItems"/>
    /// are present at once in a single menu view (a menu that closes midway is reopened).
    /// </summary>
    public static void OpenRowMenu(AppSession app, string fileName, params string[] requiredItems)
    {
        var row = Uia.Find(app.Main, ControlType.Text, fileName);
        EnsureRowSelected(row);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Uia.RightClick(row);
            try
            {
                Uia.WaitUntil(() => requiredItems.All(i => Uia.TryFindInProcess(app.Pid, ControlType.MenuItem, i) is not null),
                    $"context menu for \"{fileName}\" with all items", TimeSpan.FromSeconds(3));
                return;
            }
            catch (TimeoutException)
            {
                Uia.DismissPopups(app.Main);
                Thread.Sleep(300);
                row = Uia.Find(app.Main, ControlType.Text, fileName);
                EnsureRowSelected(row);
            }
        }
        throw new TimeoutException($"Context menu for \"{fileName}\" did not open. Current menu: [{MenuItemNames(app)}]");
    }

    /// <summary>Click the item of the displayed context menu (popup).</summary>
    public static void MenuItem(AppSession app, string item)
    {
        AutomationElement? mi;
        try
        {
            mi = Uia.Wait(() => Uia.TryFindInProcess(app.Pid, ControlType.MenuItem, item), $"menu item \"{item}\"");
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Menu item \"{item}\" not found. Current menu: [{MenuItemNames(app)}]");
        }
        Uia.Invoke(mi);
    }

    public static bool MenuItemExists(AppSession app, string item) =>
        Uia.TryFindInProcess(app.Pid, ControlType.MenuItem, item) is not null;

    /// <summary>Names of all menu items visible in the process (diagnostics).</summary>
    public static string MenuItemNames(AppSession app)
    {
        var names = new List<string>();
        var procCond = new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ProcessIdProperty, app.Pid);
        foreach (System.Windows.Automation.AutomationElement top in System.Windows.Automation.AutomationElement.RootElement.FindAll(
                     System.Windows.Automation.TreeScope.Children, procCond))
            names.AddRange(Uia.FindAll(top, ControlType.MenuItem).Select(Uia.SafeName));
        return string.Join(" | ", names);
    }

    /// <summary>Close a simple window (message dialog) with the Close/OK button if present, otherwise Escape.</summary>
    public static void CloseDialog(AutomationElement dlg)
    {
        var btn = Uia.TryFind(dlg, ControlType.Button, "Close") ?? Uia.TryFind(dlg, ControlType.Button, "OK");
        if (btn is not null) Uia.Invoke(btn);
        else if (dlg.TryGetCurrentPattern(WindowPattern.Pattern, out var wp)) ((WindowPattern)wp).Close();
    }

    /// <summary>Read all text values (Edit/Document/Text) in the window for content verification.</summary>
    public static string AllText(AutomationElement root)
    {
        var parts = new List<string>();
        foreach (var e in Uia.FindAll(root, null))
        {
            var v = Uia.Value(e);
            if (!string.IsNullOrWhiteSpace(v)) parts.Add(v);
            var n = Uia.SafeName(e);
            if (!string.IsNullOrWhiteSpace(n)) parts.Add(n);
        }
        return string.Join("\n", parts.Distinct());
    }
}
