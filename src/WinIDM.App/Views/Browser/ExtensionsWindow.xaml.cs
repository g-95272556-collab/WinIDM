using System.Windows;
using System.Windows.Controls;
using WinIDM.App.Services;
using WinIDM.Browser;

namespace WinIDM.App.Views;

/// <summary>
/// Extension manager: install from the Chrome Web Store (link or ID, with the official Claude and ChatGPT
/// extensions one click away), enable or disable, update, remove, open options or show in the side panel.
/// Every change goes through the owning <see cref="BrowserWindow"/>, which keeps the WebView2 profile in step.
/// </summary>
public partial class ExtensionsWindow : Window
{
    private sealed record Row(string Id, string Name, string Version, string State, string Access, bool Enabled, string? OptionsPage);

    private readonly Composition _c;
    private readonly BrowserWindow _browser;

    public ExtensionsWindow(Composition composition, BrowserWindow browser)
    {
        InitializeComponent();
        _c = composition;
        _browser = browser;
        Reload();
    }

    private Row? Selected => ExtensionList.SelectedItem as Row;

    private void Reload(string? selectId = null)
    {
        var rows = _c.Extensions.All.Select(x => new Row(x.Id, x.Name, x.Version, x.Enabled ? "On" : "Off",
            AccessSummary(x), x.Enabled, x.OptionsPage)).ToList();
        ExtensionList.ItemsSource = rows;
        ExtensionList.SelectedItem = rows.FirstOrDefault(r => r.Id == (selectId ?? Selected?.Id)) ?? rows.FirstOrDefault();
        StatusText.Text = rows.Count == 0 ? "No extensions installed yet." : $"{rows.Count} installed.";
    }

    private static string AccessSummary(ExtensionInfo x)
    {
        var parts = new List<string>();
        if (x.HostPermissions.Any(h => h is "<all_urls>" or "*://*/*" or "https://*/*")) parts.Add("all pages");
        if (x.Permissions.Contains("debugger")) parts.Add("control pages");
        if (x.Permissions.Contains("tabs") || x.Permissions.Contains("tabGroups")) parts.Add("tabs");
        if (x.Permissions.Contains("downloads")) parts.Add("downloads");
        if (x.Permissions.Contains("history")) parts.Add("history");
        return parts.Count == 0 ? "-" : string.Join(", ", parts);
    }

    private void ExtensionList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        EnableButton.Content = Selected is { Enabled: false } ? "Enable" : "Disable";

    private async Task RunAsync(string busy, Func<Task> action)
    {
        IsEnabled = false;
        StatusText.Text = busy;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AppLog.Error("Extensions", ex);
            StatusText.Text = ex.Message;
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async Task InstallAsync(string? input)
    {
        if (ExtensionStore.ParseStoreId(input) is not { } id)
        {
            StatusText.Text = "Paste a Chrome Web Store link or a 32-letter extension ID.";
            return;
        }
        await RunAsync("Downloading and checking the extension…", async () =>
        {
            var info = await _browser.InstallExtensionAsync(id, confirm: true);
            Reload(info?.Id);
            if (info is not null) StatusText.Text = $"{info.Name} {info.Version} installed.";
        });
    }

    private async void Install_Click(object sender, RoutedEventArgs e) => await InstallAsync(StoreInput.Text);

    private async void InstallOfficial_Click(object sender, RoutedEventArgs e) => await InstallAsync(((Button)sender).Tag as string);

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var answer = MessageBox.Show(this,
            $"Download the latest {row.Name} from the Chrome Web Store?\n\nYou may need to sign in to the extension again.",
            "Update extension", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        await RunAsync("Updating…", async () =>
        {
            var info = await _browser.InstallExtensionAsync(row.Id, confirm: false);
            Reload(row.Id);
            if (info is not null) StatusText.Text = $"{info.Name} is now {info.Version}.";
        });
    }

    private async void ToggleEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        await RunAsync(row.Enabled ? "Disabling…" : "Enabling…", async () =>
        {
            await _browser.SetExtensionEnabledAsync(row.Id, !row.Enabled);
            Reload(row.Id);
        });
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var answer = MessageBox.Show(this, $"Remove {row.Name}? Its files and stored data are deleted.",
            "Remove extension", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        await RunAsync("Removing…", async () =>
        {
            await _browser.RemoveExtensionAsync(row.Id);
            Reload();
        });
    }

    private void Options_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        if (row.OptionsPage is null)
        {
            StatusText.Text = $"{row.Name} has no options page.";
            return;
        }
        _browser.OpenExtensionPageInTab(row.Id, row.OptionsPage);
    }

    private void OpenInPanel_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        if (!row.Enabled)
        {
            StatusText.Text = $"Enable {row.Name} first.";
            return;
        }
        _browser.ShowExtensionInPanel(row.Id);
    }
}
