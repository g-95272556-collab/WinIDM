using System.Text.Json;
using WinIDM.Core.Contracts;
using WinIDM.Core.Models;

namespace WinIDM.Engine;

/// <summary>
/// Post-completion move rule. The first match wins. Either <see cref="Extension"/> or <see cref="Category"/> is used:
/// if <see cref="Extension"/> is set, it takes precedence (for example ".iso").
/// </summary>
/// <param name="Category">Category to match (for example "Video"), case-insensitive.</param>
/// <param name="Extension">File extension to match (for example ".iso").</param>
/// <param name="Folder">Destination folder.</param>
public sealed record MoveRule(string? Category, string? Extension, string Folder);

/// <summary>Details of a completed download, for App notifications.</summary>
/// <param name="Id">Download identifier.</param>
/// <param name="FileName">File name after the move.</param>
/// <param name="FullPath">Full file path after the move.</param>
/// <param name="Category">File category.</param>
/// <param name="TotalBytes">File size in bytes.</param>
/// <param name="Duration">Time from being added to completion.</param>
/// <param name="MovedTo">Destination folder if the file was moved; null otherwise.</param>
/// <param name="ChecksumVerified">True if the expected checksum was verified before completion.</param>
/// <param name="Notes">Non-critical warnings (for example a failed move, a partially recorded live stream).</param>
public sealed record DownloadCompletedInfo(
    long Id,
    string FileName,
    string FullPath,
    string Category,
    long TotalBytes,
    TimeSpan Duration,
    string? MovedTo,
    bool ChecksumVerified,
    IReadOnlyList<string> Notes);

/// <summary>Details of a failed download.</summary>
public sealed record DownloadFailedInfo(long Id, string FileName, int ErrorCode, string ErrorMessage);

/// <summary>
/// System action request for when all downloads in the queue finish. The engine does not perform this action;
/// the App must obtain user confirmation first. <see cref="Action"/> is <see cref="SystemAction.None"/> if no action is set.
/// </summary>
public sealed record SystemActionRequest(SystemAction Action, int CompletedCount, int FailedCount);

/// <summary>Post-download helpers: move rules and destination folder selection.</summary>
internal static class PostDownloadActions
{
    /// <summary>Parses the rule array JSON. Invalid JSON yields an empty list.</summary>
    public static IReadOnlyList<MoveRule> ParseMoveRules(string? json)
    {
        var rules = new List<MoveRule>();
        if (string.IsNullOrWhiteSpace(json)) return rules;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return rules;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;

                var folder = element.TryGetProperty("folder", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                if (string.IsNullOrWhiteSpace(folder)) continue;

                var category = element.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                var extension = element.TryGetProperty("extension", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                if (string.IsNullOrWhiteSpace(category) && string.IsNullOrWhiteSpace(extension)) continue;

                rules.Add(new MoveRule(
                    string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
                    string.IsNullOrWhiteSpace(extension) ? null : NormalizeExtension(extension),
                    folder.Trim()));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return rules;
    }

    /// <summary>Destination folder for a completed download, or null if the file stays where it is.</summary>
    public static string? ResolveDestination(DownloadItem item, ISettingsStore settings, IReadOnlyList<MoveRule> rules)
    {
        var extension = Path.GetExtension(item.FileName);
        foreach (var rule in rules)
        {
            var matches = rule.Extension is not null
                ? string.Equals(extension, rule.Extension, StringComparison.OrdinalIgnoreCase)
                : string.Equals(item.Category, rule.Category, StringComparison.OrdinalIgnoreCase);
            if (matches) return rule.Folder;
        }

        if (!settings.Get(SettingKeys.MoveFilesOnFinish, SettingDefaults.MoveFilesOnFinish)) return null;

        var root = settings.Get(SettingKeys.MoveFilesPath, SettingDefaults.MoveFilesPath);
        if (string.IsNullOrWhiteSpace(root)) return null;

        return settings.Get(SettingKeys.MoveFilesByCategory, SettingDefaults.MoveFilesByCategory)
            ? Path.Combine(root, DownloadCategories.DefaultFolderName(item.Category))
            : root;
    }

    /// <summary>
    /// Moves the completed file to <paramref name="destinationFolder"/>. A unique name is used if the target already exists (no overwrite).
    /// Updates <see cref="DownloadItem.SaveDirectory"/> and <see cref="DownloadItem.FileName"/> on success.
    /// Failures do not throw; the file stays where it was and a warning is returned.
    /// </summary>
    public static string? TryMove(DownloadItem item, string destinationFolder, out string? warning)
    {
        warning = null;
        var source = item.FullPath;
        if (!File.Exists(source))
        {
            warning = "The source file does not exist; move skipped.";
            return null;
        }

        if (string.Equals(Path.GetFullPath(item.SaveDirectory).TrimEnd('\\'), Path.GetFullPath(destinationFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(destinationFolder);
            var name = FileNameResolver.MakeUnique(destinationFolder, item.FileName);
            var target = Path.Combine(destinationFolder, name);
            File.Move(source, target);

            item.SaveDirectory = destinationFolder;
            item.FileName = name;
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            warning = $"Move to '{destinationFolder}' failed: {ex.Message}. The file stays in its original location.";
            return null;
        }
    }

    internal static string NormalizeExtension(string extension)
    {
        var trimmed = extension.Trim();
        return trimmed.StartsWith('.') ? trimmed.ToLowerInvariant() : "." + trimmed.ToLowerInvariant();
    }
}

/// <summary>
/// Checksum reader for text: the <c>hex  file-name</c> format (sha256sum / md5sum) or bare hex.
/// The algorithm is detected from the hex length: 64 characters = SHA-256, 32 characters = MD5.
/// </summary>
public static class ChecksumText
{
    /// <summary>Finds a checksum for <paramref name="fileName"/> in the text. A name match wins; null if none is valid.</summary>
    public static (string Algorithm, string Hex)? Parse(string text, string fileName)
    {
        ArgumentNullException.ThrowIfNull(text);

        (string Algorithm, string Hex)? bare = null;
        var bareCount = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var hex = parts[0].ToLowerInvariant();
            var algorithm = hex.Length switch
            {
                64 => "sha256",
                32 => "md5",
                _ => null,
            };
            if (algorithm is null || !hex.All(Uri.IsHexDigit)) continue;

            if (parts.Length == 1)
            {
                bare = (algorithm, hex);
                bareCount++;
                continue;
            }

            var named = parts[^1].TrimStart('*');
            if (string.Equals(named, fileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(named), fileName, StringComparison.OrdinalIgnoreCase))
            {
                return (algorithm, hex);
            }
        }

        return bareCount == 1 ? bare : null;
    }
}
