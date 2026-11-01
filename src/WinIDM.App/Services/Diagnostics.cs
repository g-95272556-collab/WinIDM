using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinIDM.Engine;

namespace WinIDM.App.Services;

/// <summary>Diagnostics text scrubber: URLs, secret keys and user profile paths are removed before writing.</summary>
public static class DiagnosticsRedactor
{
    private static readonly Regex SecretPair = new(
        @"(?i)\b(password|passwd|pass|token|secret|cookie|authorization|api[_-]?key)\b(\s*[:=]\s*)(""[^""]*""|'[^']*'|\S+)",
        RegexOptions.CultureInvariant);

    private static readonly string[] SensitiveFragments = { "pass", "token", "secret", "cookie", "client_id", "authorization" };

    public static string Text(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = UrlHelper.Redact(text);
        s = SecretPair.Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + "[redacted]");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            s = Regex.Replace(s, Regex.Escape(profile), "%USERPROFILE%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return s;
    }

    /// <summary>Keys that must not appear in diagnostics: secrets, cookies, proxy user names and OAuth client IDs.</summary>
    public static bool IsSensitiveKey(string key)
    {
        if (SettingKeys.IsSecret(key)) return true;
        if (key.EndsWith("_user", StringComparison.OrdinalIgnoreCase)) return true; // for example: idm_pref_proxy_user
        foreach (var fragment in SensitiveFragments)
        {
            if (key.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Settings without sensitive keys, with values already scrubbed.</summary>
    public static IReadOnlyDictionary<string, string> FilterSettings(IReadOnlyDictionary<string, string> snapshot) =>
        snapshot
            .Where(kv => !IsSensitiveKey(kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => Text(kv.Value), StringComparer.Ordinal);
}

/// <summary>
/// Diagnostics ZIP export: application logs (scrubbed), settings without secrets, and brief system information.
/// No download files, URL lists, cookies, passwords, tokens or databases are included.
/// </summary>
public static class DiagnosticsExporter
{
    public const int MaxLogFiles = 7;
    public const int MaxLogBytesPerFile = 1024 * 1024;

    public static IReadOnlyList<(string Name, byte[] Content)> BuildEntries(
        IReadOnlyDictionary<string, string> rawSettings,
        string logDir,
        string appVersion,
        DateTimeOffset now)
    {
        var entries = new List<(string, byte[])>();

        var readme = new StringBuilder()
            .AppendLine("WinIDM diagnostics")
            .AppendLine("Generated locally. No data is sent anywhere.")
            .AppendLine("Contents: system.json, settings.json (no secrets), logs/ (URLs and profile paths redacted).")
            .AppendLine("Not included: download URLs, download file names, cookies, passwords, OAuth tokens, databases.")
            .ToString();
        entries.Add(("README.txt", Encoding.UTF8.GetBytes(readme)));

        var system = new Dictionary<string, object?>
        {
            ["app"] = "WinIDM",
            ["version"] = appVersion,
            ["os"] = RuntimeInformation.OSDescription,
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["createdUtc"] = now.UtcDateTime.ToString("O"),
            ["settingsIncluded"] = 0,
        };
        var filtered = DiagnosticsRedactor.FilterSettings(rawSettings);
        system["settingsIncluded"] = filtered.Count;
        entries.Add(("system.json", JsonSerializer.SerializeToUtf8Bytes(system, new JsonSerializerOptions { WriteIndented = true })));
        entries.Add(("settings.json", JsonSerializer.SerializeToUtf8Bytes(filtered, new JsonSerializerOptions { WriteIndented = true })));

        if (Directory.Exists(logDir))
        {
            var files = new DirectoryInfo(logDir).GetFiles("app-*.log")
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Take(MaxLogFiles);
            foreach (var file in files)
            {
                var text = ReadTail(file.FullName, MaxLogBytesPerFile);
                var redacted = string.Join(Environment.NewLine, text.Split('\n').Select(line => DiagnosticsRedactor.Text(line.TrimEnd('\r'))));
                entries.Add(($"logs/{file.Name}", Encoding.UTF8.GetBytes(redacted)));
            }
        }

        return entries;
    }

    /// <summary>Write the ZIP through a temporary file, then replace the target.</summary>
    public static void WriteZip(string destination, IEnumerable<(string Name, byte[] Content)> entries)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = destination + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(content, 0, content.Length);
            }
        }

        File.Move(tmp, destination, overwrite: true);
    }

    private static string ReadTail(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length > maxBytes) fs.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        var text = reader.ReadToEnd();
        // Drop the first line, which may be truncated.
        if (fs.Length > maxBytes)
        {
            var nl = text.IndexOf('\n');
            if (nl >= 0) text = text[(nl + 1)..];
        }

        return text;
    }
}

/// <summary>
/// Update check. Only runs when the user sets their own URL (https) and presses the button.
/// Response format: <c>{"version":"1.2.3","url":"https://..."}</c>. No URL is embedded.
/// </summary>
public static class UpdateChecker
{
    public sealed record Result(bool Configured, bool UpdateAvailable, string Message, string? DownloadUrl);

    private const int MaxResponseBytes = 64 * 1024;

    public static async Task<Result> CheckAsync(HttpClient http, string? configuredUrl, Version current, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(configuredUrl))
            return new Result(false, false, "No update check URL is configured. Set your own URL in Settings > Advanced.", null);

        if (!Uri.TryCreate(configuredUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return new Result(true, false, "The update check URL must use https.", null);

        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            return new Result(true, false, "The update check response is too large.", null);

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > MaxResponseBytes) return new Result(true, false, "The update check response is too large.", null);

        using var doc = JsonDocument.Parse(bytes);
        if (!doc.RootElement.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String ||
            !Version.TryParse(v.GetString(), out var remote))
            return new Result(true, false, "The update check response does not contain a valid version.", null);

        string? download = null;
        if (doc.RootElement.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(u.GetString(), UriKind.Absolute, out var du) && du.Scheme == Uri.UriSchemeHttps)
            download = du.AbsoluteUri;

        var normalizedRemote = new Version(remote.Major, remote.Minor, Math.Max(0, remote.Build));
        var normalizedCurrent = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        return normalizedRemote > normalizedCurrent
            ? new Result(true, true, $"A new version {remote} is available (you have {current}).", download)
            : new Result(true, false, $"You are already running the latest version ({current}).", null);
    }
}
