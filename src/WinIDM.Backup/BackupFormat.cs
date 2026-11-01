using System.Text.Json;
using System.Text.Json.Serialization;
using WinIDM.Core.Models;

namespace WinIDM.Backup;

/// <summary>Constants for the WinIDM backup ZIP format.</summary>
internal static class BackupFormat
{
    public const string FormatId = "WinIDM.Backup";
    public const int SchemaVersion = 1;
    public const string ManifestName = "manifest.json";
    public const string SettingsName = "idm_settings.json";
    public const string DownloadPrefix = "download_info_";
    public const string JsonExtension = ".json";
    public const long MaxEntryBytes = 256L * 1024 * 1024;

    /// <summary>Setting keys not allowed to travel inside a backup (session credentials).</summary>
    public static readonly string[] SensitiveHeaderNames = { "Authorization", "Cookie", "Proxy-Authorization" };

    public static readonly JsonSerializerOptions JsonOptions = CreateOptions(writeIndented: true);

    public static readonly JsonSerializerOptions CompactJsonOptions = CreateOptions(writeIndented: false);

    public static string DownloadEntryName(long id) => $"{DownloadPrefix}{id}{JsonExtension}";

    public static string DataEntryName(string name) => name + JsonExtension;

    public static bool IsDownloadEntry(string entryName) =>
        entryName.StartsWith(DownloadPrefix, StringComparison.Ordinal)
        && entryName.EndsWith(JsonExtension, StringComparison.Ordinal);

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>Backup manifest: format identification, schema version, categories and file list.</summary>
internal sealed class BackupManifest
{
    public string Format { get; set; } = BackupFormat.FormatId;
    public int SchemaVersion { get; set; } = BackupFormat.SchemaVersion;
    public DateTimeOffset CreatedUtc { get; set; }
    public int Categories { get; set; }
    public List<string> Files { get; set; } = new();
}

/// <summary>Record of one download in download_info_{id}.json.</summary>
internal sealed class DownloadBackupRecord
{
    public int SchemaVersion { get; set; } = BackupFormat.SchemaVersion;
    public DownloadItem? Item { get; set; }
    public List<SegmentInfo> Segments { get; set; } = new();
}
