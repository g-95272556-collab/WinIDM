using System.Globalization;
using System.Text.RegularExpressions;

namespace WinIDM.Backup;

/// <summary>
/// Extra data source (bookmarks, history, passwords, adblock, userscripts, site settings).
/// Supplied by App so WinIDM.Backup does not depend on the Browser project.
/// Each name is one JSON file inside the ZIP: "{name}.json".
/// </summary>
public interface IBackupDataSource
{
    /// <summary>Export a JSON payload (object or array) for the given name. Null if there is no data.</summary>
    Task<string?> ExportAsync(string name, CancellationToken ct = default);

    /// <summary>Import a JSON payload. Throws if the payload is invalid.</summary>
    Task ImportAsync(string name, string json, RestoreMode mode, CancellationToken ct = default);
}

/// <summary>Logical names for extra data files (without the .json extension).</summary>
public static class BackupDataNames
{
    public const string Bookmarks = "bookmarks";
    public const string History = "history";
    public const string Tabs = "tabs";
    public const string Passwords = "passwords";
    public const string Adblock = "adblock";
    public const string UserScripts = "userscripts";
    public const string SiteSettings = "sitesettings";
}

/// <summary>Backup destination (local, REST, Google Drive, Dropbox).</summary>
public interface IBackupDestination
{
    string DisplayName { get; }
    Task UploadAsync(string localPath, string remoteName, CancellationToken ct = default);
    Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken ct = default);
    Task DownloadAsync(string remoteName, string localPath, CancellationToken ct = default);
    Task DeleteAsync(string remoteName, CancellationToken ct = default);
}

/// <summary>Brief details of one backup file at the destination.</summary>
public sealed record BackupEntry(string Name, DateTimeOffset ModifiedUtc, long SizeBytes);

/// <summary>Time source so the scheduler can be tested with a fake clock.</summary>
public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken ct = default);
}

public sealed class SystemClock : ISystemClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken ct = default) => Task.Delay(delay, ct);
}

/// <summary>Naming rules for backup files at the destination.</summary>
public static class BackupNames
{
    public const string Prefix = "winidm-backup-";

    private static readonly Regex SafePattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,199}\z", RegexOptions.CultureInvariant);

    public static bool IsSafe(string? name) =>
        name is not null && SafePattern.IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);

    public static string Validate(string name)
    {
        if (!IsSafe(name))
        {
            throw new ArgumentException("Invalid backup file name.", nameof(name));
        }

        return name!;
    }

    public static string NewName(DateTimeOffset utc, bool encrypted) =>
        $"{Prefix}{utc.ToUniversalTime().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}{(encrypted ? ".zip.enc" : ".zip")}";
}
