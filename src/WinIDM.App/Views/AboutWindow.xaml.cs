using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using WinIDM.App.Services;

namespace WinIDM.App.Views;

public partial class AboutWindow : Window
{
    private readonly Composition _c;

    /// <summary>Third-party licenses shipped with this build (name, license).</summary>
    public static readonly string[] ThirdPartyLicenses =
    {
        "CommunityToolkit.Mvvm 8.4.2 - MIT (https://github.com/CommunityToolkit/dotnet)",
        "MonoTorrent 3.0.2 - MIT (https://github.com/alanmcgovern/monotorrent)",
        "Microsoft.Data.Sqlite 9.0 - MIT (https://github.com/dotnet/efcore)",
        "System.Security.Cryptography.ProtectedData 9.0 - MIT (https://github.com/dotnet/runtime)",
        "System.Speech 9.0 - MIT (https://github.com/dotnet/runtime)",
        "Microsoft WebView2 SDK 1.0.4314.35 - Microsoft Software License Terms (WebView2 Runtime is installed separately by Windows)",
        "Tests: xunit 2.9.2 - Apache-2.0 (not shipped with the app)",
    };

    public AboutWindow(Composition composition)
    {
        InitializeComponent();
        _c = composition;
        var version = AppVersion();
        VersionText.Text = $"Version {version}  |  Data: {_c.DataDir}";
        LicenseBox.Text = "Third-party licenses:" + Environment.NewLine +
                          string.Join(Environment.NewLine, ThirdPartyLicenses.Select(l => "  - " + l));
    }

    public static string AppVersion()
    {
        var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(info) ? (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0") : info;
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Checking...";
        try
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            var result = await UpdateChecker.CheckAsync(_c.Http, _c.Settings.Text("app.update_url"), current);
            StatusText.Text = result.Message + (result.DownloadUrl is null ? "" : $"  Download: {result.DownloadUrl}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            AppLog.Error("Update check", ex);
            StatusText.Text = "Update check failed. See the log for details.";
        }
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export diagnostics",
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = $"winidm-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var entries = DiagnosticsExporter.BuildEntries(
                _c.RawSettings.Snapshot(), _c.LogDir, AppVersion(), DateTimeOffset.UtcNow);
            DiagnosticsExporter.WriteZip(dlg.FileName, entries);
            StatusText.Text = $"Diagnostics saved ({entries.Count} files). Review the contents before sharing.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Diagnostics export", ex);
            StatusText.Text = "Export failed: the file could not be written.";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
