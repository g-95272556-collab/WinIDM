using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WinIDM.App.Services;
using WinIDM.Browser;

namespace WinIDM.App.Views;

/// <summary>
/// User script manager: list, install (URL or file), enable/disable, per-script permissions, log, updates, and delete.
/// No update is performed silently unless the user opts in.
/// </summary>
public partial class BrowserUserScriptsWindow : Window
{
    private sealed record ScriptRow(string Id, string Name, string Version, string State, string Approved, string Source);

    private readonly UserScriptHost _host;
    private readonly BrowserPrefs _prefs;

    public BrowserUserScriptsWindow(Composition composition)
    {
        InitializeComponent();
        _host = composition.UserScripts as UserScriptHost
                ?? throw new InvalidOperationException("Script manager is not available.");
        _prefs = BrowserServices.For(composition).Prefs;
        DailyCheck.IsChecked = _prefs.Get("scripts.dailyCheck", false);
        AutoApply.IsChecked = _prefs.Get("scripts.autoApply", false);
        Refresh();
    }

    private string? SelectedId => (List.SelectedItem as ScriptRow)?.Id;

    private void Refresh(string? keepId = null)
    {
        var rows = _host.ListScripts().Select(s => new ScriptRow(
            s.Id,
            s.Name,
            s.Version,
            s.Enabled ? "Active" : "Inactive",
            s.Approved.Count == 0 ? "none" : string.Join(", ", s.Approved),
            string.IsNullOrEmpty(s.SourceUrl) ? "local" : s.SourceUrl)).ToList();
        List.ItemsSource = rows;
        if (keepId is not null) List.SelectedItem = rows.FirstOrDefault(r => r.Id == keepId);
        UpdateToggleLabel();
    }

    private void UpdateToggleLabel()
    {
        var summary = SelectedId is { } id ? _host.ListScripts().FirstOrDefault(s => s.Id == id) : null;
        ToggleButton.Content = summary is { Enabled: false } ? "Enable" : "Disable";
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateToggleLabel();

    private void Prefs_Changed(object sender, RoutedEventArgs e)
    {
        _prefs.Set("scripts.dailyCheck", DailyCheck.IsChecked == true);
        _prefs.Set("scripts.autoApply", AutoApply.IsChecked == true);
        _host.AutoApplyUpdates = AutoApply.IsChecked == true;
        StatusText.Text = "Update preferences saved.";
    }

    // ---------- Install

    private async void InstallUrl_Click(object sender, RoutedEventArgs e)
    {
        var prompt = new TextPromptWindow("Install script", ".user.js file URL (HTTPS)", "") { Owner = this };
        if (prompt.ShowDialog() != true) return;
        await InstallFromSourceUrlAsync(prompt.Value.Trim());
    }

    private async Task InstallFromSourceUrlAsync(string url)
    {
        try
        {
            StatusText.Text = "Downloading script preview...";
            var preview = await _host.PreviewFromUrlAsync(url);
            if (!BrowserScriptDialogs.ConfirmInstall(this, preview))
            {
                StatusText.Text = "Installation canceled.";
                return;
            }
            var id = _host.Install(preview);
            Refresh(id);
            StatusText.Text = "Script installed.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or UserScriptIntegrityException or InvalidDataException or TaskCanceledException)
        {
            AppLog.Error("Install user script", ex);
            StatusText.Text = "Installation failed: " + ex.Message;
        }
    }

    private async void InstallFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose a user script file",
            Filter = "User scripts (*.user.js;*.js)|*.user.js;*.js|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dlg.FileName).Length > UserScriptHost.MaxScriptBytes)
            {
                StatusText.Text = "Script file is too large.";
                return;
            }
            var text = await File.ReadAllTextAsync(dlg.FileName, new UTF8Encoding(false));
            var preview = await _host.PreviewFromSourceAsync(text, sourceUrl: null);
            if (!BrowserScriptDialogs.ConfirmInstall(this, preview)) return;
            var id = _host.Install(preview);
            Refresh(id);
            StatusText.Text = "Script installed from file.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Install script from file", ex);
            StatusText.Text = "Installation failed: " + ex.Message;
        }
    }

    // ---------- Actions on the selected script

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedId is not { } id) return;
        var summary = _host.ListScripts().First(s => s.Id == id);
        _host.SetEnabled(id, !summary.Enabled);
        Refresh(id);
    }

    private void Permissions_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedId is not { } id) return;
        var meta = _host.GetMeta(id);
        if (meta is null) return;
        var summary = _host.ListScripts().First(s => s.Id == id);
        var chosen = BrowserScriptDialogs.PickPermissions(this, meta, summary.Approved);
        if (chosen is null) return;

        foreach (var permission in summary.Approved.ToList())
            if (!chosen.Contains(permission, StringComparer.Ordinal)) _host.RevokePermission(id, permission);
        foreach (var permission in chosen)
            if (!summary.Approved.Contains(permission, StringComparer.Ordinal)) _host.ApprovePermission(id, permission);

        Refresh(id);
        StatusText.Text = "Script permissions updated.";
    }

    private void Log_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedId is not { } id) return;
        var lines = _host.GetLog(id).Select(l => $"{l.TimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} [{l.Level}] {l.Method}: {l.Message}").ToList();
        var text = lines.Count == 0 ? "No log entries for this script." : string.Join(Environment.NewLine, lines);
        new TextInfoWindow("Script log", text) { Owner = this }.Show();
    }

    private async void UpdateOne_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedId is not { } id) return;
        try
        {
            StatusText.Text = "Checking for updates...";
            var check = await _host.CheckForUpdateAsync(id);
            switch (check.State)
            {
                case UserScriptUpdateState.Available when check.Preview is { } preview:
                    if (!BrowserScriptDialogs.ConfirmInstall(this, preview))
                    {
                        StatusText.Text = "Update canceled.";
                        return;
                    }
                    _host.ApplyUpdate(preview, id);
                    Refresh(id);
                    StatusText.Text = $"Updated to version {check.RemoteVersion}.";
                    break;
                case UserScriptUpdateState.UpToDate:
                    StatusText.Text = "Script is already up to date.";
                    break;
                case UserScriptUpdateState.NoSource:
                    StatusText.Text = "This script has no update source.";
                    break;
                default:
                    StatusText.Text = "Update check failed: " + (check.Error ?? "unknown error");
                    break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or UserScriptIntegrityException or TaskCanceledException)
        {
            AppLog.Error("Update script", ex);
            StatusText.Text = "Update failed: " + ex.Message;
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Checking for updates...";
        int available = 0;
        foreach (var s in _host.ListScripts())
        {
            try
            {
                var check = await _host.CheckForUpdateAsync(s.Id);
                if (check.State == UserScriptUpdateState.Available) available++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or UserScriptIntegrityException or TaskCanceledException)
            {
                AppLog.Error("Check script updates", ex);
            }
        }
        StatusText.Text = available == 0
            ? "No updates available."
            : $"{available} scripts have updates. Select a script and press 'Update this script' to install it.";
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedId is not { } id) return;
        var name = (List.SelectedItem as ScriptRow)?.Name ?? id;
        if (MessageBox.Show(this, $"Delete script \"{name}\" and its stored values?", "Delete script",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _host.Remove(id);
        Refresh();
        StatusText.Text = "Script deleted.";
    }
}
