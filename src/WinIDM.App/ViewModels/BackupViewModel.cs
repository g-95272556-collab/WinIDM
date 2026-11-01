using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinIDM.App.Services;
using WinIDM.Backup;
using WinIDM.Core.Models;

namespace WinIDM.App.ViewModels;

/// <summary>One selectable backup category.</summary>
public sealed partial class BackupCategoryOption : ObservableObject
{
    public BackupCategoryOption(BackupCategories flag, string label, bool selected)
    {
        Flag = flag;
        Label = label;
        _isSelected = selected;
    }

    public BackupCategories Flag { get; }
    public string Label { get; }
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Backup and restore: a local file or a remote destination (Google Drive, Dropbox, REST server).
/// You can list, download and restore, or delete files at the destination. Overwrite and delete modes
/// need user confirmation. Operations can be canceled.
/// </summary>
public sealed partial class BackupViewModel : ObservableObject
{
    private readonly Composition _c;
    private readonly IShell _shell;
    private CancellationTokenSource? _runCts;

    public BackupViewModel(Composition composition, IShell shell)
    {
        _c = composition;
        _shell = shell;

        var s = _c.Settings;
        _destination = BackupDestinations.ChoiceFromKind(BackupDestinations.KindFromChoice(s.Text(BackupKeys.Destination)));
        _localFolder = s.Text(BackupKeys.LocalFolder);
        _restUrl = s.Text(BackupKeys.RestUrl);
        _googleClientId = s.Text(BackupKeys.GoogleClientId);
        _dropboxClientId = s.Text(BackupKeys.DropboxClientId);
        _scheduleEnabled = s.Bool(BackupKeys.ScheduleEnabled);
        _scheduleIntervalHours = s.Int(BackupKeys.ScheduleInterval);
        _scheduleOnOpen = s.Bool(BackupKeys.ScheduleOnOpen);
        _scheduleOnClose = s.Bool(BackupKeys.ScheduleOnClose);
        _scheduleRetention = s.Int(BackupKeys.ScheduleRetention);
        _lastStatus = s.Text(BackupKeys.LastStatus);

        var selected = (BackupCategories)s.Int(BackupKeys.ScheduleCategories);
        Categories.Add(new BackupCategoryOption(BackupCategories.Settings, "Settings", selected.HasFlag(BackupCategories.Settings)));
        Categories.Add(new BackupCategoryOption(BackupCategories.Downloads, "Download list", selected.HasFlag(BackupCategories.Downloads)));
        Categories.Add(new BackupCategoryOption(BackupCategories.Bookmarks, "Bookmarks", selected.HasFlag(BackupCategories.Bookmarks)));
        Categories.Add(new BackupCategoryOption(BackupCategories.History, "History and tabs", selected.HasFlag(BackupCategories.History)));
        Categories.Add(new BackupCategoryOption(BackupCategories.Passwords, "Passwords (requires a 12+ character password)", selected.HasFlag(BackupCategories.Passwords)));
        Categories.Add(new BackupCategoryOption(BackupCategories.DomainLimits, "Domain limits and site settings", selected.HasFlag(BackupCategories.DomainLimits)));
        Categories.Add(new BackupCategoryOption(BackupCategories.Adblock, "Ad blocker data", selected.HasFlag(BackupCategories.Adblock)));
        Categories.Add(new BackupCategoryOption(BackupCategories.UserScripts, "User scripts", selected.HasFlag(BackupCategories.UserScripts)));
        RefreshConnections();
    }

    public ObservableCollection<BackupCategoryOption> Categories { get; } = new();

    /// <summary>Backup files found at the selected destination (after List).</summary>
    public ObservableCollection<BackupEntry> RemoteBackups { get; } = new();

    public IReadOnlyList<string> DestinationChoices => BackupDestinations.Choices;

    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _overwrite;
    [ObservableProperty] private string _destination;
    [ObservableProperty] private string _localFolder;
    [ObservableProperty] private string _restUrl;
    [ObservableProperty] private string _googleClientId;
    [ObservableProperty] private string _dropboxClientId;
    [ObservableProperty] private bool _isGoogleConnected;
    [ObservableProperty] private bool _isDropboxConnected;
    [ObservableProperty] private string _googleStatus = "";
    [ObservableProperty] private string _dropboxStatus = "";
    [ObservableProperty] private bool _scheduleEnabled;
    [ObservableProperty] private int _scheduleIntervalHours;
    [ObservableProperty] private bool _scheduleOnOpen;
    [ObservableProperty] private bool _scheduleOnClose;
    [ObservableProperty] private int _scheduleRetention;
    [ObservableProperty] private string _lastStatus;
    [ObservableProperty] private BackupEntry? _selectedRemoteBackup;
    [ObservableProperty] private string _transferText = "";

    /// <summary>Backup password. Not kept after the operation finishes.</summary>
    public string Password { get; set; } = "";

    /// <summary>Password for scheduled backups (stored DPAPI-protected, only when filled in).</summary>
    public string SchedulePassword { get; set; } = "";

    /// <summary>REST browser token (stored DPAPI-protected, only when filled in).</summary>
    public string RestToken { get; set; } = "";

    public string GoogleSecret { get; set; } = "";
    public string DropboxSecret { get; set; } = "";

    public BackupCategories SelectedCategories =>
        Categories.Where(c => c.IsSelected).Aggregate(BackupCategories.None, (acc, c) => acc | c.Flag);

    /// <summary>Save destination, folder, URL and client IDs (non-secret).</summary>
    [RelayCommand]
    private void SaveDestination()
    {
        var s = _c.Settings;
        s.Set(BackupKeys.Destination, Destination);
        s.Set(BackupKeys.LocalFolder, LocalFolder.Trim());
        s.Set(BackupKeys.RestUrl, RestUrl.Trim());
        s.Set(BackupKeys.GoogleClientId, GoogleClientId.Trim());
        s.Set(BackupKeys.DropboxClientId, DropboxClientId.Trim());
        if (GoogleSecret.Length > 0) s.Set(BackupKeys.GoogleSecret, GoogleSecret.Trim());
        if (DropboxSecret.Length > 0) s.Set(BackupKeys.DropboxSecret, DropboxSecret.Trim());
        if (RestToken.Length > 0) s.Set(BackupKeys.RestToken, RestToken.Trim());
        GoogleSecret = DropboxSecret = RestToken = "";
        Status = "Destination settings saved.";
        RefreshConnections();
    }

    [RelayCommand]
    private async Task ConnectAsync(string? provider)
    {
        var kind = provider == "dropbox" ? BackupDestinationKind.Dropbox : BackupDestinationKind.GoogleDrive;
        SaveDestination();
        await RunAsync("Connect", async ct =>
        {
            await _c.Destinations.ConnectAsync(kind, ct);
            Status = $"{BackupDestinations.ChoiceFromKind(kind)} account connected.";
        });
        RefreshConnections();
    }

    [RelayCommand]
    private void Disconnect(string? provider)
    {
        var kind = provider == "dropbox" ? BackupDestinationKind.Dropbox : BackupDestinationKind.GoogleDrive;
        _c.Destinations.Disconnect(kind);
        RefreshConnections();
        Status = "Account disconnected. Local token deleted.";
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            Status = "Choose a backup target file.";
            return;
        }
        if (SelectedCategories == BackupCategories.None)
        {
            Status = "Select at least one category.";
            return;
        }
        if (Password.Length > 1024)
        {
            Status = "Password exceeds 1024 characters.";
            return;
        }
        var password = NullIfEmpty(Password);
        var kind = BackupDestinations.KindFromChoice(Destination);
        var categories = SelectedCategories;
        var filePath = FilePath;

        await RunAsync("Backup", async ct =>
        {
            if (kind == BackupDestinationKind.Local)
            {
                await _c.Backup.CreateAsync(filePath, categories, password, ct);
                Status = $"Backup saved: {filePath}";
                return;
            }

            // Remote destination: create a temporary file, upload it, then delete the local copy.
            var destination = _c.Destinations.Create(kind);
            var staging = StagingPath();
            try
            {
                await _c.Backup.CreateAsync(staging, categories, password, ct);
                var remote = BackupNames.NewName(DateTimeOffset.UtcNow, !string.IsNullOrEmpty(password));
                await UploadAsync(destination, staging, remote, ct);
                Status = $"Uploaded as {remote} to {destination.DisplayName}.";
            }
            finally
            {
                TryDelete(staging);
            }
        });
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
        {
            Status = "Choose an existing local backup file. Download it from the cloud destination first.";
            return;
        }
        if (SelectedCategories == BackupCategories.None)
        {
            Status = "Select at least one category to restore.";
            return;
        }

        var mode = Overwrite ? RestoreMode.Overwrite : RestoreMode.Merge;
        if (!ConfirmRestore(mode))
        {
            Status = "Restore canceled by the user.";
            return;
        }

        var password = NullIfEmpty(Password);
        var categories = SelectedCategories;
        var filePath = FilePath;
        await RunAsync("Restore", async ct =>
        {
            var report = await _c.Backup.RestoreWithReportAsync(filePath, categories, password, mode, ct);
            Status = "Restore completed.\n" + report.ToText();
        });
    }

    /// <summary>List backups at the selected destination (local or cloud). Read-only.</summary>
    [RelayCommand]
    private async Task ListRemoteAsync()
    {
        var kind = BackupDestinations.KindFromChoice(Destination);
        await RunAsync("List", async ct =>
        {
            var destination = _c.Destinations.Create(kind);
            var entries = await destination.ListAsync(ct);
            RemoteBackups.Clear();
            foreach (var entry in entries.OrderByDescending(e => e.Name, StringComparer.Ordinal))
            {
                RemoteBackups.Add(entry);
            }

            SelectedRemoteBackup = null;
            Status = $"{entries.Count} backups found at {destination.DisplayName}.";
        });
    }

    /// <summary>Download the selected backup to a temporary file, then restore it (with overwrite confirmation if chosen).</summary>
    [RelayCommand]
    private async Task RestoreRemoteAsync()
    {
        var selected = SelectedRemoteBackup;
        if (selected is null)
        {
            Status = "Select a backup from the list first (click List).";
            return;
        }
        if (SelectedCategories == BackupCategories.None)
        {
            Status = "Select at least one category to restore.";
            return;
        }

        var mode = Overwrite ? RestoreMode.Overwrite : RestoreMode.Merge;
        if (!ConfirmRestore(mode))
        {
            Status = "Restore canceled by the user.";
            return;
        }

        var kind = BackupDestinations.KindFromChoice(Destination);
        var password = NullIfEmpty(Password);
        var categories = SelectedCategories;
        var remote = selected.Name;
        await RunAsync("Download and restore", async ct =>
        {
            var destination = _c.Destinations.Create(kind);
            var staging = StagingPath();
            try
            {
                await DownloadAsync(destination, remote, staging, ct);
                var report = await _c.Backup.RestoreWithReportAsync(staging, categories, password, mode, ct);
                Status = $"Restore from {remote} completed.\n" + report.ToText();
            }
            finally
            {
                TryDelete(staging);
            }
        });
    }

    /// <summary>Delete the selected backup at the destination (requires confirmation).</summary>
    [RelayCommand]
    private async Task DeleteRemoteAsync()
    {
        var selected = SelectedRemoteBackup;
        if (selected is null)
        {
            Status = "Select a backup from the list first (click List).";
            return;
        }

        if (!_shell.Confirm("Delete backup",
                $"Delete '{selected.Name}' from the destination? This action cannot be undone."))
        {
            Status = "Deletion canceled by the user.";
            return;
        }

        var kind = BackupDestinations.KindFromChoice(Destination);
        var remote = selected.Name;
        await RunAsync("Delete", async ct =>
        {
            var destination = _c.Destinations.Create(kind);
            await destination.DeleteAsync(remote, ct);
            RemoteBackups.Remove(selected);
            SelectedRemoteBackup = null;
            Status = $"'{remote}' deleted from {destination.DisplayName}.";
        });
    }

    /// <summary>Cancel the running operation.</summary>
    [RelayCommand]
    private void Cancel()
    {
        var cts = _runCts;
        if (cts is not null && !cts.IsCancellationRequested)
        {
            cts.Cancel();
            Status = "Canceling…";
        }
    }

    [RelayCommand]
    private void SaveSchedule()
    {
        if (ScheduleIntervalHours < 0 || ScheduleRetention < 0)
        {
            Status = "Interval and retention cannot be negative.";
            return;
        }

        var s = _c.Settings;
        var categories = SelectedCategories & ~BackupCategories.SavedPages;
        if (SchedulePassword.Length > 0)
        {
            if (SchedulePassword.Length > 1024)
            {
                Status = "Password exceeds 1024 characters.";
                return;
            }
            s.Set(BackupKeys.SchedulePassword, SchedulePassword);
            SchedulePassword = "";
        }

        s.Set(BackupKeys.ScheduleCategories, (int)categories);
        s.Set(BackupKeys.ScheduleInterval, ScheduleIntervalHours);
        s.Set(BackupKeys.ScheduleRetention, ScheduleRetention);
        s.Set(BackupKeys.ScheduleOnOpen, ScheduleOnOpen);
        s.Set(BackupKeys.ScheduleOnClose, ScheduleOnClose);
        s.Set(BackupKeys.ScheduleEnabled, ScheduleEnabled);
        Status = string.IsNullOrEmpty(_c.BackupAutomation.StatusText)
            ? "Backup schedule saved. Scheduled backups are uploaded to the selected destination."
            : _c.BackupAutomation.StatusText;
    }

    private bool ConfirmRestore(RestoreMode mode)
    {
        var warning = mode == RestoreMode.Overwrite
            ? "OVERWRITE mode replaces the existing data for the selected categories. Current data cannot be recovered. Continue?"
            : "Restore merges data into the existing data. Continue?";
        return _shell.Confirm("Restore backup", warning);
    }

    private async Task UploadAsync(IBackupDestination destination, string localPath, string remote, CancellationToken ct)
    {
        if (destination is IProgressBackupDestination progressive)
        {
            await progressive.UploadAsync(localPath, remote, ShowProgress(), ct);
        }
        else
        {
            await destination.UploadAsync(localPath, remote, ct);
        }
    }

    private async Task DownloadAsync(IBackupDestination destination, string remote, string localPath, CancellationToken ct)
    {
        if (destination is IProgressBackupDestination progressive)
        {
            await progressive.DownloadAsync(remote, localPath, ShowProgress(), ct);
        }
        else
        {
            await destination.DownloadAsync(remote, localPath, ct);
        }
    }

    private IProgress<TransferProgress> ShowProgress() => new Progress<TransferProgress>(p =>
    {
        var done = p.BytesTransferred / 1024;
        TransferText = p.HasTotal && p.TotalBytes > 0
            ? $"Progress: {done} / {p.TotalBytes / 1024} KB ({p.BytesTransferred * 100 / p.TotalBytes}%)"
            : $"Progress: {done} KB";
    });

    private string StagingPath()
    {
        var folder = Path.Combine(_c.DataDir, "backup-staging");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{Guid.NewGuid():N}.tmp");
    }

    private void RefreshConnections()
    {
        IsGoogleConnected = _c.Destinations.IsConnected(BackupDestinationKind.GoogleDrive);
        IsDropboxConnected = _c.Destinations.IsConnected(BackupDestinationKind.Dropbox);
        GoogleStatus = StatusFor(BackupDestinationKind.GoogleDrive, IsGoogleConnected);
        DropboxStatus = StatusFor(BackupDestinationKind.Dropbox, IsDropboxConnected);
    }

    private string StatusFor(BackupDestinationKind kind, bool connected)
    {
        if (!_c.Destinations.IsClientIdConfigured(kind))
        {
            return "Status: client ID not configured yet. Enter your own OAuth client ID, then click Save destination.";
        }

        return connected ? "Status: connected" : "Status: not connected yet";
    }

    /// <summary>Run one operation with a cancellation token, busy status and uniform error handling.</summary>
    private async Task RunAsync(string action, Func<CancellationToken, Task> op)
    {
        if (IsBusy)
        {
            Status = "Another operation is running. Wait or click Cancel.";
            return;
        }

        IsBusy = true;
        using var cts = new CancellationTokenSource();
        _runCts = cts;
        try
        {
            await op(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Status = $"{action} canceled.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or BackupException
                                   or NotSupportedException or HttpRequestException or IOException)
        {
            // This message is written for the user; technical details are not shown and tokens are never included.
            Status = $"{action} failed: {ex.Message}";
            AppLog.Error(action, ex);
        }
        catch (Exception ex)
        {
            AppLog.Error(action, ex);
            Status = $"{action} failed. See the log for details.";
        }
        finally
        {
            _runCts = null;
            TransferText = "";
            Password = "";
            IsBusy = false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Temporary file; not critical.
        }
        catch (UnauthorizedAccessException)
        {
            // Temporary file; not critical.
        }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
