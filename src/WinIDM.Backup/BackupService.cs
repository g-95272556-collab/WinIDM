using System.IO.Compression;
using System.Text;
using System.Text.Json;
using WinIDM.Core.Contracts;
using WinIDM.Core.Models;

namespace WinIDM.Backup;

/// <summary>
/// ZIP backup and restore. Contents: manifest.json, idm_settings.json, download_info_{id}.json
/// and extra data files (bookmarks, history, tabs, passwords, adblock, userscripts, sitesettings).
/// If a password is supplied, the whole ZIP is encrypted with the <see cref="BackupCrypto"/> format.
/// </summary>
/// <remarks>
/// - Download credentials (Password, CookieHeader, Authorization/Cookie headers) are NOT included.
/// - <see cref="BackupCategories.SavedPages"/> is not supported yet and is skipped.
/// - The Passwords category requires a backup password.
/// </remarks>
public sealed class BackupService : IBackupService
{
    private const BackupCategories DataCategories =
        BackupCategories.Bookmarks | BackupCategories.History | BackupCategories.Passwords
        | BackupCategories.Adblock | BackupCategories.UserScripts | BackupCategories.DomainLimits;

    private readonly IDownloadRepository _repo;
    private readonly ISettingsStore _settings;
    private readonly IBackupDataSource _extra;
    private readonly string _dataDirectory;

    public BackupService(IDownloadRepository repo, ISettingsStore settings, IBackupDataSource extra, string dataDirectory)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _extra = extra ?? throw new ArgumentNullException(nameof(extra));
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = dataDirectory;
    }

    public async Task CreateAsync(string destinationPath, BackupCategories categories, string? password, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (categories == BackupCategories.None)
        {
            throw new ArgumentException("Select at least one category.", nameof(categories));
        }

        var encrypt = !string.IsNullOrEmpty(password);
        if (encrypt)
        {
            BackupCrypto.ValidatePassword(password!);
        }

        if (categories.HasFlag(BackupCategories.Passwords) && !encrypt)
        {
            throw new BackupException("The Passwords category can only be backed up with a backup password.");
        }

        var files = new List<(string Name, byte[] Data)>();

        if (categories.HasFlag(BackupCategories.Settings))
        {
            var values = _settings.Snapshot().OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            files.Add((BackupFormat.SettingsName, JsonSerializer.SerializeToUtf8Bytes(values, BackupFormat.JsonOptions)));
        }

        if (categories.HasFlag(BackupCategories.Downloads))
        {
            foreach (var item in await _repo.ListAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                var record = new DownloadBackupRecord
                {
                    Item = SanitizeForBackup(item),
                    Segments = (await _repo.LoadSegmentsAsync(item.Id, ct)).ToList(),
                };
                files.Add((BackupFormat.DownloadEntryName(item.Id),
                    JsonSerializer.SerializeToUtf8Bytes(record, BackupFormat.JsonOptions)));
            }
        }

        foreach (var (_, name) in DataUnits(categories))
        {
            ct.ThrowIfCancellationRequested();
            var json = await _extra.ExportAsync(name, ct);
            if (json is null)
            {
                continue;
            }

            EnsureJsonPayload(json, BackupFormat.DataEntryName(name)).Dispose();
            files.Add((BackupFormat.DataEntryName(name), Encoding.UTF8.GetBytes(json)));
        }

        var manifest = new BackupManifest
        {
            CreatedUtc = DateTimeOffset.UtcNow,
            Categories = (int)categories,
            Files = files.Select(f => f.Name).ToList(),
        };
        files.Insert(0, (BackupFormat.ManifestName, JsonSerializer.SerializeToUtf8Bytes(manifest, BackupFormat.JsonOptions)));

        var zipBytes = BuildZip(files);
        var payload = encrypt ? BackupCrypto.Encrypt(zipBytes, password!) : zipBytes;

        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.CreateDirectory(_dataDirectory);
        var staging = Path.Combine(_dataDirectory, $"{Guid.NewGuid():N}.partial");
        try
        {
            await File.WriteAllBytesAsync(staging, payload, ct);
            File.Move(staging, destination, overwrite: true);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    public async Task RestoreAsync(string sourcePath, BackupCategories categories, string? password, CancellationToken ct = default)
    {
        var report = await RestoreWithReportAsync(sourcePath, categories, password, RestoreMode.Merge, ct);
        if (!report.IsFullSuccess)
        {
            throw new BackupPartialRestoreException(report);
        }
    }

    /// <summary>
    /// Restore the selected categories and return a detailed report. Structural errors (magic, password,
    /// manifest, zip-slip) abort the whole restore before any change is made.
    /// An error in a single unit is recorded as <see cref="RestoreStatus.Failed"/>.
    /// </summary>
    public async Task<RestoreReport> RestoreWithReportAsync(
        string sourcePath, BackupCategories categories, string? password, RestoreMode mode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (categories == BackupCategories.None)
        {
            throw new ArgumentException("Select at least one category.", nameof(categories));
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Backup file not found.", sourcePath);
        }

        var raw = await File.ReadAllBytesAsync(sourcePath, ct);
        var zipBytes = BackupCrypto.IsEncrypted(raw) ? BackupCrypto.Decrypt(raw, password) : raw;
        if (!IsZipSignature(zipBytes))
        {
            throw new BackupFormatException("The file is not a valid WinIDM backup.");
        }

        using var zip = OpenZip(zipBytes);
        ReadManifest(zip);

        var report = new RestoreReport();

        if (categories.HasFlag(BackupCategories.SavedPages))
        {
            report.Add(new RestoreOutcome(BackupCategories.SavedPages, "Saved pages", RestoreStatus.Skipped, 0,
                "Not supported in this version."));
        }

        if (categories.HasFlag(BackupCategories.Settings))
        {
            var entry = zip.GetEntry(BackupFormat.SettingsName);
            await RunUnitAsync(report, BackupCategories.Settings, BackupFormat.SettingsName, entry is not null, () =>
            {
                var incoming = ParseSettings(ReadEntryText(entry!));
                var target = mode == RestoreMode.Overwrite
                    ? incoming
                    : MergeSettings(_settings.Snapshot(), incoming);
                _settings.Restore(target);
                return Task.FromResult((incoming.Count, "Settings restored."));
            });
        }

        if (categories.HasFlag(BackupCategories.Downloads))
        {
            var present = zip.Entries.Any(e => BackupFormat.IsDownloadEntry(e.FullName));
            await RunUnitAsync(report, BackupCategories.Downloads, "Downloads", present, () =>
                RestoreDownloadsAsync(zip, mode, ct));
        }

        foreach (var (category, name) in DataUnits(categories))
        {
            var entryName = BackupFormat.DataEntryName(name);
            var entry = zip.GetEntry(entryName);
            await RunUnitAsync(report, category, entryName, entry is not null, async () =>
            {
                var json = ReadEntryText(entry!);
                var count = CountItems(json, entryName);
                await _extra.ImportAsync(name, json, mode, ct);
                return (count, "Succeeded.");
            });
        }

        return report;
    }

    private static async Task RunUnitAsync(
        RestoreReport report, BackupCategories category, string item, bool present,
        Func<Task<(int Count, string Message)>> action)
    {
        if (!present)
        {
            report.Add(new RestoreOutcome(category, item, RestoreStatus.Skipped, 0, "Not in the backup."));
            return;
        }

        try
        {
            var (count, message) = await action();
            report.Add(new RestoreOutcome(category, item, RestoreStatus.Restored, count, message));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            report.Add(new RestoreOutcome(category, item, RestoreStatus.Failed, 0, ex.Message));
        }
    }

    private async Task<(int Count, string Message)> RestoreDownloadsAsync(ZipArchive zip, RestoreMode mode, CancellationToken ct)
    {
        var records = new List<DownloadBackupRecord>();
        foreach (var entry in zip.Entries
                     .Where(e => BackupFormat.IsDownloadEntry(e.FullName))
                     .OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            var record = DeserializeOrThrow<DownloadBackupRecord>(ReadEntryText(entry), entry.FullName);
            ValidateDownloadRecord(record, entry.FullName);
            records.Add(record);
        }

        var existing = await _repo.ListAsync(ct);
        if (mode == RestoreMode.Overwrite)
        {
            foreach (var old in existing)
            {
                ct.ThrowIfCancellationRequested();
                await _repo.DeleteAsync(old.Id, ct);
            }

            existing = Array.Empty<DownloadItem>();
        }

        var restored = 0;
        var skipped = 0;
        foreach (var record in records.OrderBy(r => r.Item!.SortOrder))
        {
            ct.ThrowIfCancellationRequested();
            var item = record.Item!;

            if (mode == RestoreMode.Merge && existing.Any(e =>
                    string.Equals(e.Url, item.Url, StringComparison.Ordinal)
                    && string.Equals(e.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }

            item.Id = 0;
            if (item.State is DownloadState.Downloading or DownloadState.Connecting or DownloadState.Merging)
            {
                item.State = DownloadState.Paused;
            }

            var newId = await _repo.InsertAsync(item, ct);
            item.Id = newId;

            var segments = record.Segments.Select(s => new SegmentInfo
            {
                DownloadId = newId,
                Index = s.Index,
                Start = s.Start,
                End = s.End,
                Downloaded = s.Downloaded,
                State = s.State == SegmentState.Active ? SegmentState.Pending : s.State,
            }).ToList();

            if (segments.Count > 0)
            {
                await _repo.SaveSegmentsAsync(newId, segments, ct);
            }

            restored++;
        }

        return (restored, $"{restored} downloads restored, {skipped} skipped (already present).");
    }

    /// <summary>Data units (category, JSON name). Tabs are placed under the History category.</summary>
    private static IEnumerable<(BackupCategories Category, string Name)> DataUnits(BackupCategories categories)
    {
        if (categories.HasFlag(BackupCategories.Bookmarks))
        {
            yield return (BackupCategories.Bookmarks, BackupDataNames.Bookmarks);
        }

        if (categories.HasFlag(BackupCategories.History))
        {
            yield return (BackupCategories.History, BackupDataNames.History);
            yield return (BackupCategories.History, BackupDataNames.Tabs);
        }

        if (categories.HasFlag(BackupCategories.Passwords))
        {
            yield return (BackupCategories.Passwords, BackupDataNames.Passwords);
        }

        if (categories.HasFlag(BackupCategories.Adblock))
        {
            yield return (BackupCategories.Adblock, BackupDataNames.Adblock);
        }

        if (categories.HasFlag(BackupCategories.UserScripts))
        {
            yield return (BackupCategories.UserScripts, BackupDataNames.UserScripts);
        }

        if (categories.HasFlag(BackupCategories.DomainLimits))
        {
            yield return (BackupCategories.DomainLimits, BackupDataNames.SiteSettings);
        }
    }

    internal static DownloadItem SanitizeForBackup(DownloadItem src) => new()
    {
        Id = src.Id,
        Kind = src.Kind,
        Url = RemoveUserInfo(src.Url),
        FinalUrl = src.FinalUrl is null ? null : RemoveUserInfo(src.FinalUrl),
        FileName = src.FileName,
        SaveDirectory = src.SaveDirectory,
        TotalBytes = src.TotalBytes,
        DownloadedBytes = src.DownloadedBytes,
        State = src.State,
        Category = src.Category,
        Referer = src.Referer,
        UserAgent = src.UserAgent,
        Headers = (src.Headers ?? new Dictionary<string, string>())
            .Where(h => !BackupFormat.SensitiveHeaderNames.Contains(h.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => h.Value, StringComparer.Ordinal),
        Username = src.Username,
        Password = null,
        CookieHeader = null,
        MaxSegments = src.MaxSegments,
        SpeedLimitBytesPerSec = src.SpeedLimitBytesPerSec,
        AcceptsRanges = src.AcceptsRanges,
        ETag = src.ETag,
        LastModified = src.LastModified,
        ExpectedSha256 = src.ExpectedSha256,
        ExpectedMd5 = src.ExpectedMd5,
        ErrorMessage = src.ErrorMessage,
        ErrorCode = src.ErrorCode,
        SortOrder = src.SortOrder,
        ScheduledAt = src.ScheduledAt,
        CreatedAt = src.CreatedAt,
        CompletedAt = src.CompletedAt,
    };

    private static string RemoveUserInfo(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
        {
            return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.AbsoluteUri;
        }

        return url;
    }

    private static void ValidateDownloadRecord(DownloadBackupRecord record, string entryName)
    {
        if (record.SchemaVersion != BackupFormat.SchemaVersion)
        {
            throw new BackupFormatException($"'{entryName}': schema version {record.SchemaVersion} is not supported.");
        }

        var item = record.Item ?? throw new BackupFormatException($"'{entryName}': the 'item' object is missing.");
        if (string.IsNullOrWhiteSpace(item.Url))
        {
            throw new BackupFormatException($"'{entryName}': empty URL.");
        }

        if (!IsSafeFileName(item.FileName))
        {
            throw new BackupFormatException($"'{entryName}': invalid file name '{item.FileName}'.");
        }

        foreach (var segment in record.Segments)
        {
            if (segment.Index < 0 || segment.Start < 0 || segment.End < segment.Start)
            {
                throw new BackupFormatException($"'{entryName}': invalid segment range {segment.Index}.");
            }
        }
    }

    private static bool IsSafeFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name != "." && name != ".."
        && Path.GetFileName(name) == name
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static Dictionary<string, string> ParseSettings(string json)
    {
        using var doc = EnsureJsonPayload(json, BackupFormat.SettingsName);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new BackupFormatException($"'{BackupFormat.SettingsName}' must be a JSON object.");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                throw new BackupFormatException("Empty setting key.");
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new BackupFormatException($"Setting value '{property.Name}' must be a string.");
            }

            result[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return result;
    }

    private static Dictionary<string, string> MergeSettings(IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, string> incoming)
    {
        var merged = new Dictionary<string, string>(current, StringComparer.Ordinal);
        foreach (var (key, value) in incoming)
        {
            merged[key] = value;
        }

        return merged;
    }

    private static int CountItems(string json, string entryName)
    {
        using var doc = EnsureJsonPayload(json, entryName);
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.GetArrayLength()
            : doc.RootElement.EnumerateObject().Count();
    }

    /// <summary>Ensure the text is valid JSON with an object or array root. The caller must Dispose the document.</summary>
    private static JsonDocument EnsureJsonPayload(string json, string entryName)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new BackupFormatException($"'{entryName}' is not valid JSON.", ex);
        }

        if (doc.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            doc.Dispose();
            throw new BackupFormatException($"'{entryName}' must be a JSON object or array.");
        }

        return doc;
    }

    private static T DeserializeOrThrow<T>(string json, string entryName) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, BackupFormat.JsonOptions)
                ?? throw new BackupFormatException($"'{entryName}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new BackupFormatException($"'{entryName}' does not match the schema: {ex.Message}", ex);
        }
    }

    private static BackupManifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(BackupFormat.ManifestName)
            ?? throw new BackupFormatException("manifest.json is missing. The file is not a complete WinIDM backup.");

        var manifest = DeserializeOrThrow<BackupManifest>(ReadEntryText(entry), BackupFormat.ManifestName);
        if (manifest.Format != BackupFormat.FormatId)
        {
            throw new BackupFormatException($"Manifest format '{manifest.Format}' is not recognized.");
        }

        if (manifest.SchemaVersion != BackupFormat.SchemaVersion)
        {
            throw new BackupFormatException($"Schema version {manifest.SchemaVersion} is not supported.");
        }

        foreach (var file in manifest.Files)
        {
            if (zip.GetEntry(file) is null)
            {
                throw new BackupFormatException($"File '{file}' is listed in the manifest but missing from the ZIP.");
            }
        }

        return manifest;
    }

    private static ZipArchive OpenZip(byte[] bytes)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException ex)
        {
            throw new BackupFormatException("Corrupt ZIP file.", ex);
        }

        try
        {
            if (zip.Entries.Count > ZipPathGuard.MaxEntries)
            {
                throw new BackupFormatException("Too many entries in the ZIP.");
            }

            foreach (var entry in zip.Entries)
            {
                ZipPathGuard.Validate(entry.FullName);
            }

            return zip;
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        if (entry.Length > BackupFormat.MaxEntryBytes)
        {
            throw new BackupFormatException($"Entry '{entry.FullName}' is too large.");
        }

        using var stream = entry.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > BackupFormat.MaxEntryBytes)
            {
                throw new BackupFormatException($"Entry '{entry.FullName}' exceeds the size limit.");
            }

            ms.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length).TrimStart('﻿');
    }

    private static byte[] BuildZip(IEnumerable<(string Name, byte[] Data)> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in files)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(data, 0, data.Length);
            }
        }

        return ms.ToArray();
    }

    private static bool IsZipSignature(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
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
}
