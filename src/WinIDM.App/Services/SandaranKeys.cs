namespace WinIDM.App.Services;

/// <summary>Backup setting keys (see <see cref="SettingsCatalog"/>, the "Backup" group).</summary>
public static class BackupKeys
{
    public const string Destination = "backup.destination";
    public const string LocalFolder = "backup.local_folder";
    public const string RestUrl = "backup.rest_url";
    public const string GoogleClientId = "backup.google_client_id";
    public const string DropboxClientId = "backup.dropbox_client_id";
    public const string GoogleSecret = "backup.google_secret";
    public const string DropboxSecret = "backup.dropbox_secret";
    public const string RestToken = "backup.rest_token_secret";
    public const string SchedulePassword = "backup.schedule_password_secret";
    public const string ScheduleEnabled = "backup.schedule.enabled";
    public const string ScheduleInterval = "backup.schedule.interval_hours";
    public const string ScheduleOnOpen = "backup.schedule.on_open";
    public const string ScheduleOnClose = "backup.schedule.on_close";
    public const string ScheduleRetention = "backup.schedule.retention";
    public const string ScheduleCategories = "backup.schedule.categories";
    public const string LastStatus = "backup.last_status";

    /// <summary>Minimum password length when the Passwords category is backed up (same as the vault export passphrase).</summary>
    public const int MinPasswordCategoryLength = 12;
}
