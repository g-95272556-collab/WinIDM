using System.Text;

namespace WinIDM.Browser;

/// <summary>State of one update check.</summary>
public enum UserScriptUpdateState
{
    /// <summary>The script has no @updateURL, @downloadURL, or install source.</summary>
    NoSource,
    /// <summary>The remote version is not newer.</summary>
    UpToDate,
    /// <summary>The remote version is newer; a preview is available.</summary>
    Available,
    /// <summary>The check failed (network, format, SHA-256, or HTTPS).</summary>
    Failed,
}

/// <summary>Result of one update check.</summary>
public sealed record UserScriptUpdateCheck(
    string ScriptId,
    UserScriptUpdateState State,
    string CurrentVersion,
    string? RemoteVersion,
    UserScriptPreview? Preview,
    string? Error,
    bool Applied = false);

/// <summary>
/// Script update checks. The source is @updateURL (or @downloadURL, or the original install URL).
/// If the update URL is a metadata file only, @downloadURL is used for the full download.
/// Versions are compared with <see cref="UserScriptVersion"/>. Installing an update keeps
/// the ID, values, enabled state, and approved permissions (the ones still declared).
/// Scheduled checks run at most once a day per enabled script.
/// </summary>
public sealed partial class UserScriptHost
{
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(24);

    private bool _autoApplyUpdates = true;

    /// <summary>
    /// If true (default), <see cref="RunScheduledUpdateChecksAsync"/> installs new versions automatically.
    /// Permissions that are not re-approved are not granted.
    /// </summary>
    public bool AutoApplyUpdates
    {
        get { lock (_gate) return _autoApplyUpdates; }
        set
        {
            lock (_gate)
            {
                _autoApplyUpdates = value;
                Save();
            }
        }
    }

    /// <summary>Check for updates to one script. Installs nothing.</summary>
    public async Task<UserScriptUpdateCheck> CheckForUpdateAsync(string id, CancellationToken ct = default)
    {
        string currentVersion;
        string? sourceUrl;
        lock (_gate)
        {
            var rec = FindById(id) ?? throw new KeyNotFoundException("Script not found.");
            var meta = _metas[id];
            currentVersion = meta.Version;
            sourceUrl = meta.UpdateUrl ?? meta.DownloadUrl ?? rec.SourceUrl;
        }

        if (sourceUrl is null || !HostUtil.TryGetHttpUri(sourceUrl, out var updateUri))
            return new UserScriptUpdateCheck(id, UserScriptUpdateState.NoSource, currentVersion, null, null, null);

        try
        {
            EnsureTransportAllowed(updateUri);

            // Step 1: download the metadata (or the full script when there is no separate @downloadURL) and compare versions.
            byte[] data = await FetchBytesAsync(_download, updateUri, MaxScriptBytes, ct).ConfigureAwait(false);
            string source = Encoding.UTF8.GetString(data).TrimStart('﻿');
            var remote = UserScriptParser.Parse(source);
            if (ComputeId(remote) != id)
                return Finish(new UserScriptUpdateCheck(id, UserScriptUpdateState.Failed, currentVersion, remote.Version, null,
                    "The script name or namespace changed; reinstall it manually."));

            if (UserScriptVersion.Compare(remote.Version, currentVersion) <= 0)
                return Finish(new UserScriptUpdateCheck(id, UserScriptUpdateState.UpToDate, currentVersion, remote.Version, null, null));

            // Step 2: the version is newer. Download the full script and build a preview (including SHA-256 pins).
            string fullUrl = remote.DownloadUrl ?? sourceUrl;
            if (!HostUtil.TryGetHttpUri(fullUrl, out var fullUri)) throw new FormatException("Invalid @downloadURL.");
            if (fullUrl != sourceUrl)
            {
                EnsureTransportAllowed(fullUri);
                data = await FetchBytesAsync(_download, fullUri, MaxScriptBytes, ct).ConfigureAwait(false);
                source = Encoding.UTF8.GetString(data).TrimStart('﻿');
                remote = UserScriptParser.Parse(source);
                if (ComputeId(remote) != id)
                    return Finish(new UserScriptUpdateCheck(id, UserScriptUpdateState.Failed, currentVersion, remote.Version, null,
                        "The script name or namespace changed; reinstall it manually."));
                if (UserScriptVersion.Compare(remote.Version, currentVersion) <= 0)
                    return Finish(new UserScriptUpdateCheck(id, UserScriptUpdateState.UpToDate, currentVersion, remote.Version, null, null));
            }

            var preview = await BuildPreviewAsync(source, fullUrl, ct).ConfigureAwait(false);
            return Finish(new UserScriptUpdateCheck(id, UserScriptUpdateState.Available, currentVersion, remote.Version, preview, null));
        }
        catch (Exception ex) when ((ex is HttpRequestException or InvalidDataException or FormatException or InvalidOperationException
                                    or UserScriptIntegrityException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            Log("ERROR", id, "update", Truncate("Update check failed: " + ex.Message, 300));
            return Finish(new UserScriptUpdateCheck(id, UserScriptUpdateState.Failed, currentVersion, null, null, ex.Message));
        }

        UserScriptUpdateCheck Finish(UserScriptUpdateCheck result)
        {
            lock (_gate)
            {
                var rec = FindById(id);
                if (rec is not null)
                {
                    rec.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
                    Save();
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Install an update preview. Throws if the ID differs or the version is not newer than the existing one.
    /// </summary>
    public string ApplyUpdate(UserScriptPreview preview, string expectedId)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedId);
        string id = ComputeId(preview.Meta);
        if (!string.Equals(id, expectedId, StringComparison.Ordinal))
            throw new ArgumentException("The preview is not for this script.", nameof(preview));

        lock (_gate)
        {
            _ = FindById(id) ?? throw new KeyNotFoundException("Script not found.");
            if (UserScriptVersion.Compare(preview.Meta.Version, _metas[id].Version) <= 0)
                throw new InvalidOperationException("The update version is not newer than the current version.");
        }
        return Install(preview);
    }

    /// <summary>
    /// Scheduled update check. Only enabled scripts that have not been checked within <see cref="UpdateInterval"/>
    /// are processed. If <see cref="AutoApplyUpdates"/> is true, new versions are installed and marked <c>Applied</c>.
    /// </summary>
    public async Task<IReadOnlyList<UserScriptUpdateCheck>> RunScheduledUpdateChecksAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        List<string> due;
        bool autoApply;
        lock (_gate)
        {
            autoApply = _autoApplyUpdates;
            due = _scripts
                .Where(r => r.Enabled && (r.LastUpdateCheckUtc is null || now - r.LastUpdateCheckUtc.Value >= UpdateInterval))
                .Select(r => r.Id)
                .ToList();
        }

        var results = new List<UserScriptUpdateCheck>(due.Count);
        foreach (string id in due)
        {
            ct.ThrowIfCancellationRequested();
            var check = await CheckForUpdateAsync(id, ct).ConfigureAwait(false);
            if (check.State == UserScriptUpdateState.Available && autoApply && check.Preview is not null)
            {
                ApplyUpdate(check.Preview, id);
                check = check with { Applied = true };
            }
            results.Add(check);
        }
        return results;
    }
}
