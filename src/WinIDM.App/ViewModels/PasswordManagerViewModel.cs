using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using WinIDM.App.Services;
using WinIDM.Browser;

namespace WinIDM.App.ViewModels;

/// <summary>One display entry: origin and username only. Passwords are not stored in the list.</summary>
public sealed record PasswordRow(string Origin, string Username);

/// <summary>
/// Password manager: list, search, add/edit/delete, generator, CSV import/export.
/// Passwords are only read from the vault when the user asks for them (Edit or Copy).
/// </summary>
public sealed partial class PasswordManagerViewModel : ObservableObject, IDisposable
{
    /// <summary>Delay before the clipboard is cleared after a password is copied.</summary>
    public static readonly TimeSpan ClipboardClearDelay = TimeSpan.FromSeconds(30);

    /// <summary>CSV import file size limit (10 MB).</summary>
    private const long MaxImportBytes = 10 * 1024 * 1024;

    private readonly Composition _c;
    private readonly IShell _shell;
    private readonly List<PasswordRow> _all = new();
    private readonly DispatcherTimer _sessionTimer;
    private readonly DispatcherTimer _clipboardTimer;
    private string? _copiedSecret;
    private bool _wasUnlocked;
    private PasswordRow? _editing;   // original entry while editing (to change origin/username)
    private bool _disposed;

    public PasswordManagerViewModel(Composition composition, IShell shell)
    {
        _c = composition;
        _shell = shell;
        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _sessionTimer.Tick += (_, _) => OnSessionTick();
        _sessionTimer.Start();
        _clipboardTimer = new DispatcherTimer { Interval = ClipboardClearDelay };
        _clipboardTimer.Tick += (_, _) => { _clipboardTimer.Stop(); ClearClipboardIfUnchanged(); };
    }

    public ObservableCollection<PasswordRow> Visible { get; } = new();

    [ObservableProperty] private bool _isInitialized;
    [ObservableProperty] private bool _isUnlocked;
    [ObservableProperty] private bool _showSetup;
    [ObservableProperty] private bool _showUnlock;
    [ObservableProperty] private bool _showContent;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private PasswordRow? _selected;
    [ObservableProperty] private string _formOrigin = "";
    [ObservableProperty] private string _formUsername = "";
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private int _generatorLength = 20;
    [ObservableProperty] private bool _useLower = true;
    [ObservableProperty] private bool _useUpper = true;
    [ObservableProperty] private bool _useDigits = true;
    [ObservableProperty] private bool _useSymbols = true;

    partial void OnSearchChanged(string value) => ApplyFilter();

    /// <summary>Reload vault state and the account list. Called on open and after changes.</summary>
    public async Task RefreshAsync()
    {
        if (_disposed) return;
        try
        {
            IsInitialized = _c.Vault.IsInitialized;
            IsUnlocked = IsInitialized && _c.Vault.IsUnlocked;
            ShowSetup = !IsInitialized;
            ShowUnlock = IsInitialized && !IsUnlocked;
            ShowContent = IsUnlocked;
            _wasUnlocked = IsUnlocked;

            _all.Clear();
            if (IsUnlocked)
            {
                var list = await _c.Vault.ListAsync();
                foreach (var (origin, username) in list) _all.Add(new PasswordRow(origin, username));
                Status = _all.Count == 0 ? "No passwords saved yet." : "";
            }
            else
            {
                Status = IsInitialized ? "Password manager is locked." : "Password manager is not set up yet.";
            }
            ApplyFilter();
        }
        catch (Exception ex)
        {
            AppLog.Error("Password manager: refresh", ex);
            Status = "Failed to load the list. See the log for details.";
        }
    }

    private void ApplyFilter()
    {
        var q = (Search ?? "").Trim();
        Visible.Clear();
        foreach (var r in _all)
        {
            if (q.Length == 0
                || r.Origin.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Username.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                Visible.Add(r);
            }
        }
        CountText = $"{Visible.Count} of {_all.Count} accounts";
    }

    /// <summary>Lock the vault and clear all password displays.</summary>
    public void Lock()
    {
        _c.Vault.Lock();
        ClearForm();
        _ = RefreshAsync();
    }

    /// <summary>Clear the add/edit form (the password is cleared by the window).</summary>
    public void ClearForm()
    {
        FormOrigin = "";
        FormUsername = "";
        IsEditing = false;
        _editing = null;
    }

    /// <summary>Fill the form for editing. Returns the current password (displayed by the window only).</summary>
    public async Task<string?> BeginEditAsync(PasswordRow row)
    {
        var password = await _c.Vault.GetPasswordAsync(row.Origin, row.Username);
        if (password is null)
        {
            Status = "The entry no longer exists.";
            await RefreshAsync();
            return null;
        }
        FormOrigin = row.Origin;
        FormUsername = row.Username;
        IsEditing = true;
        _editing = row;
        Status = "Editing account. Save to update.";
        return password;
    }

    /// <summary>
    /// Save the form. When editing, <see cref="PasswordVault.EditAsync"/> changes the origin/username/password
    /// and keeps the creation date; adding a new entry uses <see cref="PasswordVault.AddAsync"/>.
    /// </summary>
    public async Task<bool> SaveAsync(string password)
    {
        var origin = (FormOrigin ?? "").Trim();
        var username = (FormUsername ?? "").Trim();
        if (origin.Length == 0 || username.Length == 0)
        {
            Status = "Site and username are required.";
            return false;
        }
        if (password.Length == 0)
        {
            Status = "Password is required.";
            return false;
        }
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            Status = "Site must be an http or https URL.";
            return false;
        }

        try
        {
            if (_editing is { } old)
                await _c.Vault.EditAsync(old.Origin, old.Username, origin, username, password);
            else
                await _c.Vault.AddAsync(origin, username, password);
            Status = "Saved.";
            ClearForm();
            await RefreshAsync();
            return true;
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;
            return false;
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
            return false;
        }
    }

    /// <summary>Save a password from the browser offer (called by the browser after the user chooses Save).</summary>
    public async Task<bool> SaveCredentialAsync(string origin, string username, string password)
    {
        try
        {
            await _c.Vault.AddAsync(origin, username, password);
            await RefreshAsync();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Status = ex.Message;
            return false;
        }
    }

    public async Task DeleteSelectedAsync()
    {
        if (Selected is not { } row) return;
        if (!_shell.Confirm("Delete password", $"Delete the password for {row.Username} at {row.Origin}? This action cannot be undone."))
            return;
        try
        {
            await _c.Vault.RemoveAsync(row.Origin, row.Username);
            Status = "Entry deleted.";
            if (_editing == row) ClearForm();
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
        }
        await RefreshAsync();
    }

    /// <summary>Copy the password of the selected entry. The clipboard is cleared after 30 seconds if unchanged.</summary>
    public async Task CopySelectedAsync()
    {
        if (Selected is not { } row) return;
        var password = await _c.Vault.GetPasswordAsync(row.Origin, row.Username);
        if (password is null)
        {
            Status = "No password available for this site.";
            return;
        }
        try
        {
            Clipboard.SetText(password);
            _copiedSecret = password;
            _clipboardTimer.Stop();
            _clipboardTimer.Start();
            Status = $"Password copied. The clipboard is cleared in {ClipboardClearDelay.TotalSeconds:0} seconds.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            Status = "The clipboard is in use. Try again.";
        }
    }

    private void ClearClipboardIfUnchanged()
    {
        try
        {
            if (_copiedSecret is not null && Clipboard.ContainsText() && Clipboard.GetText() == _copiedSecret)
                Clipboard.Clear();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            // Clipboard is locked by another app; leave it.
        }
        finally
        {
            _copiedSecret = null;
        }
    }

    /// <summary>Generate a password from the current options. Returns null when no character set is selected.</summary>
    public string? GeneratePassword()
    {
        if (!(UseLower || UseUpper || UseDigits || UseSymbols))
        {
            Status = "Select at least one character set.";
            return null;
        }
        var length = Math.Clamp(GeneratorLength, PasswordGenerator.MinLength, PasswordGenerator.MaxLength);
        GeneratorLength = length;
        Status = "Password generated.";
        return PasswordGenerator.Generate(length, new PasswordGeneratorOptions(UseLower, UseUpper, UseDigits, UseSymbols));
    }

    /// <summary>Import CSV from Chrome, Firefox, Safari, Bitwarden or 1Password.</summary>
    public async Task ImportCsvAsync(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxImportBytes)
            {
                Status = "CSV file is too large (10 MB limit).";
                return;
            }
            var csv = await File.ReadAllTextAsync(path, Encoding.UTF8);
            var result = await _c.Vault.ImportCsvAsync(csv);
            Status = $"Import {result.Format}: {result.Imported} imported, {result.Skipped} skipped.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Status = $"Import failed: {ex.Message}";
        }
        await RefreshAsync();
    }

    /// <summary>Export raw CSV (plain-text passwords) after user confirmation.</summary>
    public async Task ExportCsvAsync(string path)
    {
        if (!_shell.Confirm("Export passwords",
                "The exported CSV contains plain-text passwords. Store it somewhere safe and delete it after use. Continue?"))
            return;
        try
        {
            var csv = await _c.Vault.ExportRawCsvAsync(acknowledgeRawExport: true);
            await File.WriteAllTextAsync(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Status = "Export completed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = $"Export failed: {ex.Message}";
        }
    }

    private void OnSessionTick()
    {
        if (_disposed) return;
        var unlocked = IsInitialized && _c.Vault.IsUnlocked;
        if (_wasUnlocked && !unlocked)
        {
            // Session ended (5 minutes idle) or locked elsewhere.
            ClearForm();
            _ = RefreshAsync();
            Status = "Session ended. Unlock again to see passwords.";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessionTimer.Stop();
        _clipboardTimer.Stop();
        ClearClipboardIfUnchanged();
    }
}
