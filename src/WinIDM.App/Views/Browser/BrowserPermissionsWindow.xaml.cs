using System.Windows;
using WinIDM.App.Services;

namespace WinIDM.App.Views;

/// <summary>
/// Per-site stored permission management (camera, microphone, location, notifications, popups) and
/// the SSL error exception list for the current session.
/// </summary>
public partial class BrowserPermissionsWindow : Window
{
    private sealed record Row(string Origin, string Kind, string Decision, string KindKey, bool Allow);

    private readonly SitePermissionStore _store;

    public BrowserPermissionsWindow(SitePermissionStore store, IEnumerable<string> sslOverrides)
    {
        InitializeComponent();
        _store = store;
        SslList.ItemsSource = sslOverrides.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();
        Refresh();
    }

    private Row? Selected => PermissionList.SelectedItem as Row;

    private void Refresh()
    {
        PermissionList.ItemsSource = _store.All
            .OrderBy(p => p.Origin, StringComparer.OrdinalIgnoreCase)
            .Select(p => new Row(p.Origin, KindLabel(p.Kind), p.Allow ? "Allow" : "Block", p.Kind, p.Allow))
            .ToList();
    }

    /// <summary>Toggle the remembered decision (allow to block or the reverse).</summary>
    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        _store.Set(row.Origin, row.KindKey, !row.Allow);
        Refresh();
        StatusText.Text = "Decision updated.";
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        _store.Remove(row.Origin, row.KindKey);
        Refresh();
        StatusText.Text = "Permission deleted. The site will ask again.";
    }

    private void RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        if (_store.All.Count == 0) return;
        if (MessageBox.Show(this, "Delete all remembered site permissions?", "Site permissions",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var p in _store.All.ToList()) _store.Remove(p.Origin, p.Kind);
        Refresh();
        StatusText.Text = "All permissions deleted.";
    }

    /// <summary>English label for WebView2 and popup permission kinds.</summary>
    public static string KindLabel(string kind) => kind switch
    {
        "Microphone" => "Microphone",
        "Camera" => "Camera",
        "Geolocation" => "Location",
        "Notifications" => "Notifications",
        "Popup" => "Popups and new tabs",
        "ClipboardRead" => "Read clipboard",
        _ => kind,
    };
}
