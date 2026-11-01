namespace WinIDM.Engine;

/// <summary>
/// Per-site/domain limits enforced by the engine (WebsiteLimit and DomainPartLimit, see 01 sections 5.3 and 5.4).
/// </summary>
/// <param name="Domain">The referenced domain (lowercase, no scheme).</param>
/// <param name="Limit">Maximum number of active downloads for that domain.</param>
/// <param name="DownloadLink">The limit applies to the download link host.</param>
/// <param name="RefererLink">The limit applies to the referer host.</param>
public sealed record SiteLimit(string Domain, int Limit, bool DownloadLink, bool RefererLink);

/// <summary>Source of per-domain and per-site limits for the engine scheduler.</summary>
public interface ISiteRuleProvider
{
    /// <summary>Part (segment) limit for a host; 0 means no limit.</summary>
    Task<int> GetDomainPartLimitAsync(string host, CancellationToken ct = default);

    /// <summary>Concurrent download limit for a host; null means no limit.</summary>
    Task<SiteLimit?> GetSiteLimitAsync(string host, CancellationToken ct = default);
}
