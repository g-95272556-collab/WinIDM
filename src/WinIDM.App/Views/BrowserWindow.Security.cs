using System.Windows;
using WinIDM.App.Services;
using WinIDM.App.ViewModels;

namespace WinIDM.App.Views;

/// <summary>Site permissions and the password audit entry point from the browser menu.</summary>
public partial class BrowserWindow
{
    /// <summary>Permission key for popups and new tabs (not a WebView2 permission).</summary>
    private const string PopupKind = "Popup";

    private void OpenPermissions_Click(object sender, RoutedEventArgs e)
    {
        var overrides = _sslAllowedHosts.ToList();
        new BrowserPermissionsWindow(_c.SitePermissions, overrides) { Owner = this }.Show();
    }

    private void OpenPasswordAudit_Click(object sender, RoutedEventArgs e) =>
        new BrowserPasswordAuditWindow(_c) { Owner = this }.Show();
}
