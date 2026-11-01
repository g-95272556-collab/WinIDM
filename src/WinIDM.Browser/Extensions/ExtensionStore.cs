using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WinIDM.Browser;

/// <summary>An installed extension as WinIDM sees it (read from its manifest).</summary>
public sealed record ExtensionInfo(
    string Id,
    string Name,
    string Version,
    string Folder,
    bool Enabled,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> HostPermissions,
    IReadOnlyList<string> PanelPages,
    string? PopupPage,
    string? OptionsPage)
{
    /// <summary>The page shown in the side panel: the side panel page, else the toolbar popup.</summary>
    public string? PanelPage => PanelPages.Count > 0 ? PanelPages[0] : PopupPage;
}

/// <summary>
/// Extensions installed from the Chrome Web Store into a WinIDM folder. Packages are verified with
/// <see cref="CrxPackage"/> before anything is written; each version is unpacked into its own folder because
/// WebView2 drops an extension whose files change after it was added. The browser profile itself is updated by the
/// caller (<c>CoreWebView2Profile.AddBrowserExtensionAsync</c>).
/// </summary>
public sealed class ExtensionStore
{
    private const long MaxPackageBytes = 256L * 1024 * 1024;
    private const long MaxUnpackedBytes = 1024L * 1024 * 1024;
    private const int MaxEntries = 50_000;
    private static readonly Regex IdPattern = new("(?<![a-p])([a-p]{32})(?![a-p])", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions IndexJson = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions ManifestJson = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly string _root;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly List<Entry> _entries;

    private sealed record Entry(string Id, string Version, string Folder, bool Enabled);

    public ExtensionStore(string root, HttpClient http)
    {
        _root = root;
        _http = http;
        Directory.CreateDirectory(root);
        _entries = LoadIndex();
    }

    private string IndexPath => Path.Combine(_root, "extensions.json");

    /// <summary>Installed extensions whose folder still exists.</summary>
    public IReadOnlyList<ExtensionInfo> All
    {
        get
        {
            List<Entry> copy;
            lock (_gate) copy = _entries.ToList();
            var list = new List<ExtensionInfo>();
            foreach (var e in copy.Where(e => File.Exists(Path.Combine(e.Folder, "manifest.json"))))
            {
                try { list.Add(Describe(e.Id, e.Folder, e.Enabled)); }
                catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { /* broken folder: skipped */ }
            }
            return list;
        }
    }

    public ExtensionInfo? Find(string id) => All.FirstOrDefault(e => e.Id == id);

    /// <summary>An extension ID from a bare ID or any Chrome Web Store link; null when there is none.</summary>
    public static string? ParseStoreId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var m = IdPattern.Match(input.Trim().ToLowerInvariant());
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>The Web Store update service link that redirects to the CRX for <paramref name="id"/>.</summary>
    public static string DownloadUrl(string id, string chromeVersion) =>
        "https://clients2.google.com/service/update2/crx?response=redirect&acceptformat=crx3" +
        $"&prodversion={Uri.EscapeDataString(chromeVersion)}&x=id%3D{id}%26installsource%3Dondemand%26uc";

    /// <summary>Download, verify and unpack an extension from the Chrome Web Store.</summary>
    public async Task<ExtensionInfo> InstallFromStoreAsync(string id, string chromeVersion, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(DownloadUrl(id, chromeVersion), HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The Chrome Web Store answered {(int)response.StatusCode} for {id}.");
        if (response.Content.Headers.ContentLength > MaxPackageBytes)
            throw new InvalidDataException("The extension package is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxPackageBytes) throw new InvalidDataException("The extension package is too large.");
        }
        if (buffer.Length == 0) throw new InvalidDataException($"The Chrome Web Store has no package for {id}.");
        return InstallPackage(buffer.ToArray(), id);
    }

    /// <summary>Verify and unpack a CRX file. The previous version stays on disk until <see cref="PruneOldVersions"/>.</summary>
    public ExtensionInfo InstallPackage(byte[] crx, string? expectedId = null)
    {
        var pkg = CrxPackage.Parse(crx, expectedId);
        var staging = Path.Combine(_root, pkg.Id, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            Unzip(pkg.Zip, staging);
            var manifestPath = Path.Combine(staging, "manifest.json");
            if (!File.Exists(manifestPath)) throw new InvalidDataException("The package has no manifest.json.");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath), documentOptions: ManifestJson)?.AsObject()
                           ?? throw new InvalidDataException("manifest.json is not a JSON object.");
            // The key keeps the Web Store ID when the folder is loaded unpacked; sites and the extension's own
            // servers recognise the extension by that ID.
            if (manifest["key"] is null)
            {
                manifest["key"] = Convert.ToBase64String(pkg.PublicKey);
                File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            var version = SafeName(manifest["version"]?.GetValue<string>() ?? "0");
            var folder = Path.Combine(_root, pkg.Id, version);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.Move(staging, folder);

            bool enabled;
            lock (_gate)
            {
                enabled = _entries.FirstOrDefault(e => e.Id == pkg.Id)?.Enabled ?? true;
                _entries.RemoveAll(e => e.Id == pkg.Id);
                _entries.Add(new Entry(pkg.Id, version, folder, enabled));
                SaveIndex();
            }
            return Describe(pkg.Id, folder, enabled);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>Delete unpacked versions other than the current one (call after the profile uses the new folder).</summary>
    public void PruneOldVersions(string id)
    {
        string? current;
        lock (_gate) current = _entries.FirstOrDefault(e => e.Id == id)?.Folder;
        var dir = Path.Combine(_root, id);
        if (current is null || !Directory.Exists(dir)) return;
        foreach (var d in Directory.GetDirectories(dir))
        {
            if (string.Equals(Path.GetFullPath(d), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(d, recursive: true); }
            catch (IOException) { /* still in use by the browser; removed next time */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            var i = _entries.FindIndex(e => e.Id == id);
            if (i < 0) return;
            _entries[i] = _entries[i] with { Enabled = enabled };
            SaveIndex();
        }
    }

    /// <summary>Forget the extension and delete its files (call after it was removed from the profile).</summary>
    public void Remove(string id)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Id == id);
            SaveIndex();
        }
        var dir = Path.Combine(_root, id);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------- manifest

    /// <summary>Read what WinIDM needs from an unpacked extension folder.</summary>
    public static ExtensionInfo Describe(string id, string folder, bool enabled)
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")), documentOptions: ManifestJson)!.AsObject();
        static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        static List<string> Arr(JsonNode? n) => n is JsonArray a ? a.Select(Str).Where(s => s.Length > 0).ToList() : new();

        var name = Localize(folder, Str(root["default_locale"]), Str(root["name"]));
        var action = root["action"] ?? root["browser_action"];
        var popup = NullIfEmpty(Str(action?["default_popup"]));
        var options = NullIfEmpty(Str(root["options_ui"]?["page"])) ?? NullIfEmpty(Str(root["options_page"]));

        // The declared side panel first, then other side panel pages the extension may switch to at run time.
        // Loading placeholders go last: they wait for a toolbar click that a WebView2 host does not have.
        var panels = new List<string>();
        if (NullIfEmpty(Str(root["side_panel"]?["default_path"])) is { } declared) panels.Add(declared);
        foreach (var f in Directory.EnumerateFiles(folder, "*.html", SearchOption.TopDirectoryOnly)
                     .Select(Path.GetFileName).OfType<string>()
                     .Where(f => Regex.IsMatch(f, "side[-_]?panel", RegexOptions.IgnoreCase))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            if (!panels.Contains(f)) panels.Add(f);
        }
        panels = panels.OrderBy(p => p.Contains("loading", StringComparison.OrdinalIgnoreCase)).ToList();

        return new ExtensionInfo(id, name.Length > 0 ? name : id, Str(root["version"]), folder, enabled,
            Arr(root["permissions"]), Arr(root["host_permissions"]), panels, popup, options);
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s.TrimStart('/');

    /// <summary>Resolve <c>__MSG_name__</c> from the default locale.</summary>
    private static string Localize(string folder, string locale, string text)
    {
        var m = Regex.Match(text, "^__MSG_(\\w+)__$");
        if (!m.Success || locale.Length == 0) return text;
        var path = Path.Combine(folder, "_locales", SafeName(locale), "messages.json");
        if (!File.Exists(path)) return text;
        try
        {
            var messages = JsonNode.Parse(File.ReadAllText(path), documentOptions: ManifestJson)?.AsObject();
            var entry = messages?.FirstOrDefault(p => string.Equals(p.Key, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)).Value;
            return entry?["message"]?.GetValue<string>() ?? text;
        }
        catch (JsonException) { return text; }
    }

    // ---------- files

    private static void Unzip(byte[] zip, string target)
    {
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("The extension package has too many files.");
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The extension package contains a path outside its folder.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            total += entry.Length;
            if (total > MaxUnpackedBytes) throw new InvalidDataException("The extension package is too large when unpacked.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
        }
    }

    private static string SafeName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return clean.Length == 0 || clean is "." or ".." ? "_" : clean;
    }

    private List<Entry> LoadIndex()
    {
        try
        {
            return File.Exists(IndexPath)
                ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(IndexPath)) ?? new()
                : new();
        }
        catch (JsonException) { return new(); }
    }

    private void SaveIndex()
    {
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, IndexJson));
        File.Move(tmp, IndexPath, overwrite: true);
    }
}
