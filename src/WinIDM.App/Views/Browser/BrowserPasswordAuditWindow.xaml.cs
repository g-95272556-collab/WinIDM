using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using WinIDM.App.Services;
using WinIDM.Browser;

namespace WinIDM.App.Views;

/// <summary>
/// Password audit: account list, search, strength (evaluated locally), duplicates (keyed HMAC fingerprint),
/// delete, and export. Encrypted export requires a passphrase. Raw export requires an explicit warning.
/// </summary>
public partial class BrowserPasswordAuditWindow : Window
{
    private sealed record Row(string Origin, string User, string Strength, string Duplicate, string Created, string Modified);

    private readonly Composition _c;
    private readonly PasswordVault _vault;
    private Dictionary<(string Origin, string User), string> _strength = new();
    private Dictionary<(string Origin, string User), int> _groups = new();

    public BrowserPasswordAuditWindow(Composition composition)
    {
        InitializeComponent();
        _c = composition;
        _vault = composition.Vault;
        Loaded += async (_, _) => await EnsureUnlockedAndLoadAsync();
    }

    private async Task EnsureUnlockedAndLoadAsync()
    {
        if (!_vault.IsInitialized)
        {
            StatusText.Text = "The password manager is not set up yet. Set it up from the Password manager menu.";
            return;
        }
        if (!_vault.IsUnlocked && !await VaultPrompts.UnlockAsync(_c, this))
        {
            Close();
            return;
        }
        await ReloadAsync();
    }

    private async Task ReloadAsync(string? query = null)
    {
        var entries = string.IsNullOrWhiteSpace(query)
            ? await _vault.ListAllAsync()
            : await _vault.SearchAsync(query);
        List.ItemsSource = entries.Select(e =>
        {
            var key = (e.Origin, e.Username);
            return new Row(
                e.Origin,
                e.Username,
                _strength.TryGetValue(key, out var s) ? s : "not analyzed",
                _groups.TryGetValue(key, out var g) ? "group " + g : "",
                e.Created.LocalDateTime.ToString("yyyy-MM-dd"),
                e.Modified.LocalDateTime.ToString("yyyy-MM-dd"));
        }).ToList();
        StatusText.Text = $"{entries.Count} accounts.";
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await ReloadAsync(SearchBox.Text);

    private async void Search_Click(object sender, RoutedEventArgs e) => await ReloadAsync(SearchBox.Text);

    /// <summary>
    /// Evaluates strength per account and groups duplicates. Passwords are read one at a time and never stored
    /// in a persistent variable. Only scores and labels are displayed.
    /// </summary>
    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked) return;
        StatusText.Text = "Analyzing...";
        var entries = await _vault.ListAllAsync();
        var strength = new Dictionary<(string, string), string>();
        foreach (var entry in entries)
        {
            var password = await _vault.GetPasswordAsync(entry.Origin, entry.Username);
            if (password is null) continue;
            var result = PasswordStrength.Evaluate(password);
            strength[(entry.Origin, entry.Username)] = result.Score switch
            {
                0 => "Very weak",
                1 => "Weak",
                2 => "Medium",
                3 => "Strong",
                _ => "Very strong",
            };
        }

        var groups = await _vault.FindDuplicatePasswordsAsync();
        var groupMap = new Dictionary<(string, string), int>();
        int index = 0;
        foreach (var group in groups)
        {
            index++;
            foreach (var key in group.Entries) groupMap[(key.Origin, key.Username)] = index;
        }

        _strength = strength;
        _groups = groupMap;
        await ReloadAsync(SearchBox.Text);
        StatusText.Text = $"Analysis complete: {groups.Count} duplicate groups. Change or delete weak or duplicate accounts.";
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not Row row) return;
        if (MessageBox.Show(this, $"Delete the password for {row.User} on {row.Origin}?", "Delete account",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await _vault.RemoveAsync(row.Origin, row.User);
            _strength.Remove((row.Origin, row.User));
            await ReloadAsync(SearchBox.Text);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            AppLog.Error("Delete password account", ex);
            StatusText.Text = "Delete failed: " + ex.Message;
        }
    }

    private async void ExportEncrypted_Click(object sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked) return;
        var pass = BrowserSecretPrompt.Ask(this, "Encrypted export",
            $"Export passphrase (minimum {PasswordVault.MinExportPassphraseLength} characters). Keep this passphrase; without it the file cannot be opened.");
        if (pass is null) return;
        if (pass.Length < PasswordVault.MinExportPassphraseLength)
        {
            StatusText.Text = $"Passphrase is too short (minimum {PasswordVault.MinExportPassphraseLength} characters).";
            return;
        }
        var confirm = BrowserSecretPrompt.Ask(this, "Encrypted export", "Confirm passphrase:");
        if (confirm != pass)
        {
            StatusText.Text = "Passphrases do not match. Export canceled.";
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "Encrypted password export",
            Filter = "WinIDM vault file (*.json)|*.json",
            FileName = "winidm-passwords.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var envelope = await _vault.ExportEncryptedAsync(pass);
            await File.WriteAllTextAsync(dlg.FileName, envelope, new UTF8Encoding(false));
            StatusText.Text = "Encrypted export complete.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Encrypted export", ex);
            StatusText.Text = "Export failed. See the log.";
        }
    }

    private async void ExportRaw_Click(object sender, RoutedEventArgs e)
    {
        if (!_vault.IsUnlocked) return;
        if (MessageBox.Show(this, PasswordVault.RawExportWarning + "\n\nContinue with the raw export?", "Raw CSV export",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var dlg = new SaveFileDialog
        {
            Title = "Raw CSV export (NOT ENCRYPTED)",
            Filter = "CSV files (*.csv)|*.csv",
            FileName = "winidm-passwords-raw.csv",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var csv = await _vault.ExportRawCsvAsync(acknowledgeRawExport: true);
            await File.WriteAllTextAsync(dlg.FileName, csv, new UTF8Encoding(false));
            StatusText.Text = "Raw CSV export complete. Delete the file after use.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Raw CSV export", ex);
            StatusText.Text = "Export failed. See the log.";
        }
    }
}
