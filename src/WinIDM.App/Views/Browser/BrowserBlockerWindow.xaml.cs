using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using WinIDM.App.Services;
using WinIDM.Browser;

namespace WinIDM.App.Views;

/// <summary>
/// Blocker manager: filter lists (download on demand), host sources, whitelist, element rules,
/// and statistics. Every filter list change rebuilds the blocker engine.
/// </summary>
public partial class BrowserBlockerWindow : Window
{
    private sealed record FilterRow(string Id, string Name, string State, string Updated, string Size, string Source, bool Custom);
    private sealed record HostRow(string Name, string State, string Source);
    private sealed record RuleRow(string Host, string Selector);

    private readonly Composition _c;
    private readonly BrowserServices _svc;

    public BrowserBlockerWindow(Composition composition)
    {
        InitializeComponent();
        _c = composition;
        _svc = BrowserServices.For(composition);
        RefreshAll();
    }

    private void RefreshAll()
    {
        RefreshFilters();
        RefreshHosts();
        RefreshRules();
        RefreshStats();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs) return;
        RefreshAll();
    }

    // ---------- Filter lists

    private void RefreshFilters()
    {
        FilterList.ItemsSource = _svc.Filters.Lists().Select(s => new FilterRow(
            s.Id,
            s.Name,
            s.Enabled ? "Active" : "Inactive",
            s.LastUpdatedUtc is { } u ? u.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "never",
            s.CachedBytes > 0 ? $"{s.CachedBytes / 1024} KB" : "-",
            s.LastError is { Length: > 0 } err ? "Error: " + err : s.Url,
            s.IsCustom)).ToList();
    }

    private FilterRow? SelectedFilter => FilterList.SelectedItem as FilterRow;

    private void FilterToggle_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFilter is not { } row) return;
        var state = _svc.Filters.Get(row.Id);
        if (state is null) return;
        _svc.Filters.SetEnabled(row.Id, !state.Enabled);
        _svc.RebuildEngine();
        RefreshFilters();
    }

    /// <summary>Downloads start only when this button is pressed. Selected item, or all enabled lists if nothing is selected.</summary>
    private async void FilterUpdate_Click(object sender, RoutedEventArgs e)
    {
        var ids = SelectedFilter is { } row
            ? new List<string> { row.Id }
            : _svc.Filters.Lists().Where(s => s.Enabled).Select(s => s.Id).ToList();
        if (ids.Count == 0)
        {
            StatusText.Text = "No enabled lists. Select a list and enable it first.";
            return;
        }

        StatusText.Text = $"Downloading {ids.Count} lists...";
        int ok = 0, failed = 0;
        foreach (var id in ids)
        {
            var result = await _svc.Filters.UpdateAsync(id);
            if (result.Status == FilterListUpdateStatus.Failed) failed++;
            else ok++;
        }
        _svc.RebuildEngine();
        RefreshFilters();
        StatusText.Text = $"Download complete: {ok} succeeded, {failed} failed. Engine rebuilt.";
    }

    private void FilterAddCustom_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt("List name", "Filter list name");
        if (string.IsNullOrWhiteSpace(name)) return;
        var url = Prompt("List URL", "Filter list HTTPS URL");
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            _svc.Filters.AddCustomSource(name.Trim(), url.Trim());
            RefreshFilters();
            StatusText.Text = "Custom source added. Download happens only when you press 'Download / update'.";
        }
        catch (ArgumentException ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void FilterRemoveCustom_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFilter is not { Custom: true } row)
        {
            StatusText.Text = "Select a custom source to delete. Catalog lists cannot be deleted.";
            return;
        }
        if (MessageBox.Show(this, $"Delete source \"{row.Name}\"?", "Delete source", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;
        _svc.Filters.RemoveCustomSource(row.Id);
        _svc.RebuildEngine();
        RefreshFilters();
    }

    private void FilterRebuild_Click(object sender, RoutedEventArgs e)
    {
        _svc.RebuildEngine();
        StatusText.Text = "Blocker engine rebuilt from the local cache.";
        RefreshStats();
    }

    // ---------- Host sources and whitelist

    private void RefreshHosts()
    {
        var urls = _svc.Rules.Data.HostSources.ToDictionary(s => s.Name, s => s.Url ?? "local", StringComparer.OrdinalIgnoreCase);
        HostList.ItemsSource = _svc.Hosts.Sources().Select(s => new HostRow(
            s.Name,
            s.Enabled ? "Active" : "Inactive",
            urls.TryGetValue(s.Name, out var u) ? u : "local")).ToList();
        WhitelistList.ItemsSource = _svc.Hosts.Whitelist().OrderBy(h => h, StringComparer.Ordinal).ToList();
    }

    private void HostAdd_Click(object sender, RoutedEventArgs e) => _ = AddHostAsync();

    private async Task AddHostAsync()
    {
        var name = Prompt("Host source name", "Source name (example: hosts-default)");
        if (string.IsNullOrWhiteSpace(name)) return;
        var url = Prompt("Host source URL", "HTTPS URL of a hosts file or domain list");
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            StatusText.Text = "Downloading host source...";
            int count = await _svc.AddHostSourceFromUrlAsync(name.Trim(), url.Trim());
            RefreshHosts();
            StatusText.Text = $"Host source added: {count} domains.";
        }
        catch (Exception ex) when (ex is ArgumentException or HttpRequestException or InvalidDataException or TaskCanceledException)
        {
            AppLog.Error("Add host source", ex);
            StatusText.Text = "Failed to add host source: " + ex.Message;
        }
    }

    private void HostToggle_Click(object sender, RoutedEventArgs e)
    {
        if (HostList.SelectedItem is not HostRow row) return;
        _svc.SetHostSourceEnabled(row.Name, !row.State.Equals("Active", StringComparison.Ordinal));
        RefreshHosts();
    }

    private void HostRemove_Click(object sender, RoutedEventArgs e)
    {
        if (HostList.SelectedItem is not HostRow row) return;
        _svc.RemoveHostSource(row.Name);
        RefreshHosts();
    }

    private void WhitelistAdd_Click(object sender, RoutedEventArgs e)
    {
        var domain = Prompt("Whitelist", "Domain that is not blocked (example: example.com)");
        if (string.IsNullOrWhiteSpace(domain)) return;
        _svc.AddHostWhitelist(domain.Trim());
        RefreshHosts();
    }

    private void WhitelistRemove_Click(object sender, RoutedEventArgs e)
    {
        if (WhitelistList.SelectedItem is not string domain) return;
        _svc.RemoveHostWhitelist(domain);
        RefreshHosts();
    }

    // ---------- Element rules

    private void RefreshRules()
    {
        RuleList.ItemsSource = _svc.Rules.Data.ManualCosmetic
            .SelectMany(kv => kv.Value.Select(sel => new RuleRow(kv.Key, sel)))
            .OrderBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void RuleRemove_Click(object sender, RoutedEventArgs e)
    {
        if (RuleList.SelectedItem is not RuleRow row) return;
        _svc.Rules.RemoveManualSelector(row.Host, row.Selector);
        RefreshRules();
    }

    // ---------- Statistics

    private void RefreshStats()
    {
        var engine = _svc.Engine.Stats;
        var hosts = _svc.Hosts.GetStats(20);
        var lines = new List<string>
        {
            $"Network rules        : {engine.NetworkRules}",
            $"Exceptions (@@)      : {engine.ExceptionRules}",
            $"Host rules           : {engine.HostRules}",
            $"Cosmetic rules       : {engine.CosmeticRules}",
            $"Unsupported          : {engine.UnsupportedRules}",
            $"Bad filters          : {engine.BadFilteredRules}",
            $"Whitelisted pages    : {engine.WhitelistedPages}",
            "",
            $"Active host domains  : {_svc.Hosts.ActiveDomainCount}",
            $"Total host blocks    : {hosts.TotalBlocked}",
            $"Since                : {(hosts.SinceUtc is { } since ? since.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "-")}",
            "",
            "Top blocked domains:",
        };
        lines.AddRange(hosts.TopDomains.Select(d => $"  {d.Domain,-40} {d.Count,8}"));
        if (hosts.TopDomains.Count == 0) lines.Add("  (none)");
        StatsText.Text = string.Join(Environment.NewLine, lines);
    }

    private void StatsReset_Click(object sender, RoutedEventArgs e)
    {
        _svc.Hosts.ResetStats();
        RefreshStats();
    }

    private string? Prompt(string title, string label)
    {
        var dlg = new TextPromptWindow(title, label, "") { Owner = this };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }
}
