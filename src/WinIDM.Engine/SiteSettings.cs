namespace WinIDM.Engine;

/// <summary>
/// Per-site settings (the <c>site_settings</c> table). A null field means "use the global settings".
/// </summary>
/// <param name="Domain">Domain (lowercase, no scheme). The longest matching domain wins.</param>
/// <param name="UserAgent">Site-specific User-Agent. The value "null" drops the User-Agent header (same as downloads).</param>
/// <param name="Cookies">Cookies in <c>Cookie</c> header format. Secret: stored encrypted with DPAPI.</param>
/// <param name="SaveDirectory">Default download folder for this site.</param>
/// <param name="UseForSubdomain">If true, these settings also apply to subdomains.</param>
public sealed record SiteSettings(
    string Domain,
    string? UserAgent = null,
    string? Cookies = null,
    string? SaveDirectory = null,
    bool UseForSubdomain = true);

/// <summary>Source of per-site settings for the engine (implemented by <see cref="SqliteDownloadRepository"/>).</summary>
public interface ISiteSettingsProvider
{
    /// <summary>Settings for a host (longest domain match); null if none.</summary>
    Task<SiteSettings?> GetSiteSettingsAsync(string host, CancellationToken ct = default);
}
