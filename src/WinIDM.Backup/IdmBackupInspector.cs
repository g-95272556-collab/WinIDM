using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace WinIDM.Backup;

/// <summary>Type of backup file container detected.</summary>
public enum BackupContainerKind { Unknown, Encrypted1DmCrypt, PlainZip }

/// <summary>Content authentication status. <see cref="NotApplicable"/> for a plain ZIP.</summary>
public enum InspectAuthStatus { NotApplicable, NotAttempted, PasswordMissing, Authenticated, RejectedOrTampered, UnsupportedHeader }

/// <summary>Fields of the 42-byte header. Salt and nonce lengths are fixed by the study (16 and 12).</summary>
public sealed record IdmHeaderInfo(
    string Magic,
    ushort Version,
    uint Iterations,
    bool IterationsWithinIdmRange,
    int SaltLength,
    int NonceLength,
    long CiphertextLength,
    bool TagPresent);

/// <summary>One ZIP entry. <see cref="Origin"/> is the classification based on the name (and brief content for download_info).</summary>
public sealed record IdmZipEntryInfo(
    string Name,
    long UncompressedBytes,
    long CompressedBytes,
    bool PathSafe,
    string? PathIssue,
    string Origin);

/// <summary>Backup file inspection report. Writes nothing to disk.</summary>
public sealed record IdmBackupReport(
    string? SourcePath,
    long FileBytes,
    BackupContainerKind Kind,
    IdmHeaderInfo? Header,
    InspectAuthStatus Authentication,
    bool ZipReadable,
    IReadOnlyList<IdmZipEntryInfo> Entries,
    string LikelySource,
    IReadOnlyList<string> Findings)
{
    /// <summary>Text summary for display or logging (without secret contents).</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"File: {SourcePath ?? "(bytes in memory)"}");
        sb.AppendLine($"Size: {FileBytes} bytes");
        sb.AppendLine($"Type: {KindText(Kind)}");
        if (Header is { } h)
        {
            sb.AppendLine($"Header: magic={h.Magic}, version={h.Version}, iterations={h.Iterations}, salt={h.SaltLength} bytes, nonce={h.NonceLength} bytes, ciphertext={h.CiphertextLength} bytes, tag={(h.TagPresent ? "16 bytes" : "none")}");
        }

        sb.AppendLine($"Authentication: {AuthText(Authentication)}");
        sb.AppendLine($"Detected source: {LikelySource}");
        if (Entries.Count > 0)
        {
            sb.AppendLine($"ZIP entries ({Entries.Count}):");
            foreach (var e in Entries)
            {
                var safety = e.PathSafe ? string.Empty : $" [UNSAFE: {e.PathIssue}]";
                sb.AppendLine($"  - {e.Name} ({e.UncompressedBytes} bytes) [{e.Origin}]{safety}");
            }
        }

        if (Findings.Count > 0)
        {
            sb.AppendLine("Findings:");
            foreach (var f in Findings)
            {
                sb.AppendLine($"  - {f}");
            }
        }

        return sb.ToString();
    }

    private static string KindText(BackupContainerKind kind) => kind switch
    {
        BackupContainerKind.Encrypted1DmCrypt => "1DMCRYPT (encrypted)",
        BackupContainerKind.PlainZip => "Plain ZIP (unencrypted)",
        _ => "unknown",
    };

    private static string AuthText(InspectAuthStatus status) => status switch
    {
        InspectAuthStatus.NotAttempted => "not attempted (no password supplied)",
        InspectAuthStatus.PasswordMissing => "empty password",
        InspectAuthStatus.Authenticated => "succeeded (GCM tag valid)",
        InspectAuthStatus.RejectedOrTampered => "failed (wrong password or data modified)",
        InspectAuthStatus.UnsupportedHeader => "not attempted (header outside the study)",
        _ => "not applicable",
    };
}

/// <summary>
/// Inspector for the structure of IDM+ and WinIDM backup files. Reads the magic, version, iterations, sizes and
/// field lengths without a password. If a password is supplied, it tries to decrypt in memory and list the ZIP entries.
/// Entries are never extracted; unsafe paths are only reported. Format errors are not thrown:
/// they are recorded in <see cref="IdmBackupReport.Findings"/>.
/// </summary>
/// <remarks>
/// Entry name classification is based on decompiled IDM+ code (see FORMAT.md). It shows a POSSIBILITY,
/// not proof; real IDM+ samples are needed for verification.
/// </remarks>
public static class IdmBackupInspector
{
    /// <summary>File size limit for inspecting in memory.</summary>
    public const long MaxFileBytes = 512L * 1024 * 1024;

    private const string OriginWinIdm = "WinIDM";
    private const string OriginIdm = "IDM+ (name from decompiled code)";
    private const string OriginShared = "same name: WinIDM and IDM+";
    private const string OriginUnknown = "unknown";

    /// <summary>Exact IDM+ entry names found in <c>lc.java</c>/<c>of1.java</c>/<c>q61.java</c>.</summary>
    private static readonly HashSet<string> IdmExactNames = new(StringComparer.Ordinal)
    {
        "ui_settings.json",
        "key_browser_menu_strip.json",
        "key_bookmarks.json",
        "key_history.json",
        "key_last_saved_tabs_2.json",
        "key_passwords.json",
        "key_saved_pages.json",
        "key_domain_part_limit.json",
        "key_website_limit.json",
        "key_adblock_whitelist2.json",
        "key_adblock_filter_sources.json",
        "key_adblock_manual_filters.json",
        "key_adblock_bad_filters.json",
        "key_drm_video_play_response.json",
        "key_disable_context_menu_response.json",
        "key_site_settings.json",
        "key_domain_to_app_mapping.json",
        "browser_passwords.json",
        "settings.txt",
    };

    private static readonly string[] IdmPrefixes = { "key_", "shortcut_icon_", "page_", "icon_" };

    private static readonly HashSet<string> WinIdmExactNames = new(StringComparer.Ordinal)
    {
        BackupFormat.ManifestName,
        "bookmarks.json",
        "history.json",
        "tabs.json",
        "passwords.json",
        "adblock.json",
        "userscripts.json",
        "sitesettings.json",
    };

    /// <summary>Inspect a file on disk. I/O errors are thrown; format errors are reported.</summary>
    public static IdmBackupReport InspectFile(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Backup file not found.", path);
        }

        if (info.Length > MaxFileBytes)
        {
            throw new ArgumentException($"File is too large to inspect (limit {MaxFileBytes / (1024 * 1024)} MB).", nameof(path));
        }

        return Inspect(File.ReadAllBytes(path), password, info.FullName);
    }

    /// <summary>Inspect bytes in memory.</summary>
    public static IdmBackupReport Inspect(byte[] data, string? password = null, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var findings = new List<string>();
        var entries = new List<IdmZipEntryInfo>();

        if (data.Length == 0)
        {
            findings.Add("Empty file.");
            return Report(sourcePath, 0, BackupContainerKind.Unknown, null, InspectAuthStatus.NotApplicable, false, entries, findings);
        }

        if (BackupCrypto.IsEncrypted(data))
        {
            return InspectEncrypted(data, password, sourcePath, findings, entries);
        }

        if (IsZip(data))
        {
            var readable = ReadZipEntries(data, entries, findings);
            return Report(sourcePath, data.Length, BackupContainerKind.PlainZip, null, InspectAuthStatus.NotApplicable, readable, entries, findings);
        }

        findings.Add("Not 1DMCRYPT and not a ZIP (no recognized magic).");
        return Report(sourcePath, data.Length, BackupContainerKind.Unknown, null, InspectAuthStatus.NotApplicable, false, entries, findings);
    }

    private static IdmBackupReport InspectEncrypted(
        byte[] data, string? password, string? sourcePath, List<string> findings, List<IdmZipEntryInfo> entries)
    {
        findings.Add("1DMCRYPT magic detected (42-byte header, matches the IDM+ code study).");

        if (data.Length < BackupCrypto.HeaderSize)
        {
            findings.Add($"Incomplete header: {data.Length} bytes, 42 required.");
            return Report(sourcePath, data.Length, BackupContainerKind.Encrypted1DmCrypt, null,
                InspectAuthStatus.UnsupportedHeader, false, entries, findings);
        }

        var version = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8, 2));
        var iterations = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(10, 4));
        var inRange = iterations is >= BackupCrypto.MinIterations and <= BackupCrypto.MaxIterations;
        var cipherLength = data.Length - BackupCrypto.HeaderSize - BackupCrypto.TagSize;
        var tagPresent = cipherLength >= 0;

        var header = new IdmHeaderInfo(
            "1DMCRYPT",
            version,
            iterations,
            inRange,
            BackupCrypto.SaltSize,
            BackupCrypto.NonceSize,
            Math.Max(0, cipherLength),
            tagPresent);

        findings.Add(version == BackupCrypto.FormatVersion
            ? "Version 1: matches the IDM+ code studied."
            : $"Version {version}: does not match version 1 in the study.");
        findings.Add(iterations == BackupCrypto.Iterations
            ? "PBKDF2 iterations 600000: the value written by IDM+ code."
            : inRange
                ? $"PBKDF2 iterations {iterations}: within the range accepted by IDM+ code (1000-1000000)."
                : $"PBKDF2 iterations {iterations}: outside the range accepted by IDM+ code (1000-1000000).");

        if (!tagPresent)
        {
            findings.Add("No 16-byte GCM tag at the end of the file: truncated file.");
            return Report(sourcePath, data.Length, BackupContainerKind.Encrypted1DmCrypt, header,
                InspectAuthStatus.UnsupportedHeader, false, entries, findings);
        }

        findings.Add("16-byte GCM tag present (length only; verification requires the password).");

        if (password is null)
        {
            findings.Add("No password supplied; the ZIP contents were not inspected.");
            return Report(sourcePath, data.Length, BackupContainerKind.Encrypted1DmCrypt, header,
                InspectAuthStatus.NotAttempted, false, entries, findings);
        }

        if (password.Length == 0)
        {
            findings.Add("Empty password; the ZIP contents were not inspected.");
            return Report(sourcePath, data.Length, BackupContainerKind.Encrypted1DmCrypt, header,
                InspectAuthStatus.PasswordMissing, false, entries, findings);
        }

        if (version != BackupCrypto.FormatVersion || !inRange)
        {
            findings.Add("Decryption attempt skipped because the header is outside the study.");
            return Report(sourcePath, data.Length, BackupContainerKind.Encrypted1DmCrypt, header,
                InspectAuthStatus.UnsupportedHeader, false, entries, findings);
        }

        byte[] plain;
        try
        {
            plain = BackupCrypto.Decrypt(data, password);
        }
        catch (BackupAuthenticationException)
        {
            findings.Add("Decryption failed: wrong password or the data has been modified (invalid GCM tag).");
            return Report(sourcePath, data.Length, BackupContainerKind.Encrypted1DmCrypt, header,
                InspectAuthStatus.RejectedOrTampered, false, entries, findings);
        }
        catch (ArgumentException ex)
        {
            findings.Add($"Invalid password: {ex.Message}");
            return Report(sourcePath, data.Length, BackupContainerKind.Encrypted1DmCrypt, header,
                InspectAuthStatus.RejectedOrTampered, false, entries, findings);
        }
        catch (BackupFormatException ex)
        {
            findings.Add($"Header rejected: {ex.Message}");
            return Report(sourcePath, data.Length, BackupContainerKind.Encrypted1DmCrypt, header,
                InspectAuthStatus.UnsupportedHeader, false, entries, findings);
        }

        findings.Add("GCM tag valid: contents decrypted (bytes are kept in memory only).");
        bool readable;
        try
        {
            readable = ReadZipEntries(plain, entries, findings);
        }
        finally
        {
            // Contents may include sensitive data (for example browser passwords): clear them after reading.
            CryptographicOperations.ZeroMemory(plain);
        }

        return Report(sourcePath, data.Length, BackupContainerKind.Encrypted1DmCrypt, header,
            InspectAuthStatus.Authenticated, readable, entries, findings);
    }

    private static bool ReadZipEntries(byte[] zipBytes, List<IdmZipEntryInfo> entries, List<string> findings)
    {
        if (!IsZip(zipBytes))
        {
            findings.Add("Contents are not a ZIP (no PK signature).");
            return false;
        }

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(zipBytes, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            findings.Add("ZIP is corrupt or unreadable.");
            return false;
        }

        using (zip)
        {
            var total = zip.Entries.Count;
            if (total > ZipPathGuard.MaxEntries)
            {
                findings.Add($"Too many entries ({total}); only the first {ZipPathGuard.MaxEntries} were inspected.");
            }

            foreach (var entry in zip.Entries.Take(ZipPathGuard.MaxEntries))
            {
                string? issue = null;
                try
                {
                    ZipPathGuard.Validate(entry.FullName);
                }
                catch (BackupSecurityException ex)
                {
                    issue = ex.Message;
                }

                entries.Add(new IdmZipEntryInfo(
                    entry.FullName,
                    entry.Length,
                    entry.CompressedLength,
                    issue is null,
                    issue,
                    ClassifyEntry(entry)));
            }

            findings.Add($"Valid ZIP with {total} entries.");
            foreach (var unsafeEntry in entries.Where(e => !e.PathSafe))
            {
                findings.Add($"Entry '{unsafeEntry.Name}' has an unsafe path; not extracted.");
            }
        }

        return true;
    }

    private static string ClassifyEntry(ZipArchiveEntry entry)
    {
        var name = entry.FullName;
        if (WinIdmExactNames.Contains(name))
        {
            return OriginWinIdm;
        }

        if (name == BackupFormat.SettingsName)
        {
            return OriginShared;
        }

        if (name.StartsWith(BackupFormat.DownloadPrefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
        {
            // WinIDM and IDM+ both name files download_info_{id}.json; tell them apart by the first character.
            var first = FirstNonWhitespaceByte(entry);
            return first switch
            {
                (byte)'<' => OriginIdm + ", XML content",
                (byte)'{' => OriginWinIdm + ", JSON content",
                _ => OriginUnknown,
            };
        }

        if (IdmExactNames.Contains(name) || IdmPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
        {
            return OriginIdm;
        }

        return OriginUnknown;
    }

    private static byte? FirstNonWhitespaceByte(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            var buffer = new byte[64];
            var read = stream.Read(buffer, 0, buffer.Length);
            for (var i = 0; i < read; i++)
            {
                if (!char.IsWhiteSpace((char)buffer[i]) && buffer[i] != 0xEF && buffer[i] != 0xBB && buffer[i] != 0xBF)
                {
                    return buffer[i];
                }
            }
        }
        catch (InvalidDataException)
        {
            // ZIP is corrupt for this entry; classified as unknown.
        }

        return null;
    }

    private static bool IsZip(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4;

    private static IdmBackupReport Report(
        string? sourcePath,
        long fileBytes,
        BackupContainerKind kind,
        IdmHeaderInfo? header,
        InspectAuthStatus auth,
        bool zipReadable,
        List<IdmZipEntryInfo> entries,
        List<string> findings)
    {
        var source = DetermineSource(entries, kind, auth);
        return new IdmBackupReport(sourcePath, fileBytes, kind, header, auth, zipReadable, entries, source, findings);
    }

    private static string DetermineSource(IReadOnlyList<IdmZipEntryInfo> entries, BackupContainerKind kind, InspectAuthStatus auth)
    {
        if (entries.Any(e => e.Name == BackupFormat.ManifestName && e.Origin == OriginWinIdm))
        {
            return "WinIDM (manifest.json present)";
        }

        if (entries.Any(e => e.Origin.StartsWith(OriginIdm, StringComparison.Ordinal)))
        {
            return "Probably IDM+ (entry names match the decompiled code; not yet verified against a real sample)";
        }

        if (kind == BackupContainerKind.Encrypted1DmCrypt && auth != InspectAuthStatus.Authenticated)
        {
            return "Uncertain (header structure matches IDM+; a password is needed to inspect the entries)";
        }

        return "Uncertain";
    }
}
