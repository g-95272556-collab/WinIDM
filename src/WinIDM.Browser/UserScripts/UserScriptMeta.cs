using System.Text.RegularExpressions;

namespace WinIDM.Browser;

/// <summary>Named resource (@resource name url).</summary>
public sealed record UserScriptResource(string Name, string Url);

/// <summary>Metadata parsed from the ==UserScript== block.</summary>
public sealed record UserScriptMeta(
    string Name,
    string? Namespace,
    string Version,
    string? Author,
    string? Description,
    IReadOnlyList<string> Matches,
    IReadOnlyList<string> Includes,
    IReadOnlyList<string> Excludes,
    IReadOnlyList<string> ExcludeMatches,
    IReadOnlyList<string> Requires,
    IReadOnlyList<UserScriptResource> Resources,
    IReadOnlyList<string> Grants,
    IReadOnlyList<string> Connects,
    string RunAt,
    bool NoFrames,
    string? UpdateUrl = null,
    string? DownloadUrl = null);

/// <summary>Parser for the ==UserScript== header. No script code is executed during parsing.</summary>
public static class UserScriptParser
{
    private static readonly Regex HeaderLine = new(@"^\s*//\s*@([A-Za-z0-9_-]+)(?:\s+(.*?))?\s*$", RegexOptions.Compiled);

    private static readonly HashSet<string> ValidRunAt = new(StringComparer.Ordinal)
    {
        "document-start", "document-body", "document-end", "document-idle", "context-menu",
    };

    public static UserScriptMeta Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var lines = source.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, l => l.Trim() == "// ==UserScript==");
        if (start < 0) throw new FormatException("==UserScript== block not found.");
        int end = Array.FindIndex(lines, start + 1, l => l.Trim() == "// ==/UserScript==");
        if (end < 0) throw new FormatException("==/UserScript== terminator not found.");

        string? name = null, ns = null, version = null, author = null, description = null, runAt = null;
        string? updateUrl = null, downloadUrl = null;
        bool noFrames = false;
        var matches = new List<string>();
        var includes = new List<string>();
        var excludes = new List<string>();
        var excludeMatches = new List<string>();
        var requires = new List<string>();
        var resources = new List<UserScriptResource>();
        var grants = new List<string>();
        var connects = new List<string>();

        for (int i = start + 1; i < end; i++)
        {
            var m = HeaderLine.Match(lines[i]);
            if (!m.Success) continue;
            string key = m.Groups[1].Value.ToLowerInvariant();
            string value = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";

            switch (key)
            {
                case "name": name = value; break;
                case "namespace": ns = value; break;
                case "version": version = value; break;
                case "author": author = value; break;
                case "description": description = value; break;
                case "run-at": runAt = value.ToLowerInvariant(); break;
                case "noframes": noFrames = true; break;
                case "updateurl": if (value.Length > 0) updateUrl = value; break;
                case "downloadurl": if (value.Length > 0) downloadUrl = value; break;
                case "match": if (value.Length > 0) matches.Add(value); break;
                case "include": if (value.Length > 0) includes.Add(value); break;
                case "exclude": if (value.Length > 0) excludes.Add(value); break;
                case "exclude-match": if (value.Length > 0) excludeMatches.Add(value); break;
                case "connect": if (value.Length > 0) connects.Add(value.ToLowerInvariant()); break;
                case "grant":
                    if (value.Length > 0 && !value.Equals("none", StringComparison.OrdinalIgnoreCase))
                        grants.Add(value);
                    break;
                case "require":
                    if (value.Length > 0) requires.Add(value);
                    break;
                case "resource":
                {
                    var parts = value.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2) throw new FormatException("@resource must be in the form 'name url'.");
                    resources.Add(new UserScriptResource(parts[0], parts[1].Trim()));
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(name)) throw new FormatException("@name is required.");
        foreach (string r in requires)
        {
            if (!HostUtil.TryGetHttpUri(r.Split('#')[0], out _))
                throw new FormatException($"@require must be an http(s) URL: {r}");
        }
        foreach (var res in resources)
        {
            if (!HostUtil.TryGetHttpUri(res.Url.Split('#')[0], out _))
                throw new FormatException($"@resource must be an http(s) URL: {res.Name}");
        }

        if (updateUrl is not null && !HostUtil.TryGetHttpUri(updateUrl, out _))
            throw new FormatException("@updateURL must be an http(s) URL.");
        if (downloadUrl is not null && !HostUtil.TryGetHttpUri(downloadUrl, out _))
            throw new FormatException("@downloadURL must be an http(s) URL.");

        string run = runAt is not null && ValidRunAt.Contains(runAt) ? runAt : "document-end";

        return new UserScriptMeta(
            name!.Trim(),
            ns,
            string.IsNullOrWhiteSpace(version) ? "0" : version!,
            author,
            description,
            matches,
            includes,
            excludes,
            excludeMatches,
            requires,
            resources,
            grants,
            connects,
            run,
            noFrames,
            updateUrl,
            downloadUrl);
    }
}
