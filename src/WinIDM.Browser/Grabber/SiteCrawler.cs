using System.Text;
using System.Text.RegularExpressions;

namespace WinIDM.Browser;

/// <summary>Host scope for crawling.</summary>
public enum CrawlHostScope
{
    /// <summary>Only the exact host of the start URL.</summary>
    ExactHost,
    /// <summary>The same host and all subdomains of the registrable domain (for example: img.example.com for www.example.com).</summary>
    IncludeSubdomains,
    /// <summary>Any host.</summary>
    AnyHost,
}

/// <summary>
/// Parameters for one crawl session. Depth and page-count limits bound the recursion.
/// File filters (extension, regex, size, date) are applied only to the discovered file list.
/// Size and date filters require a HEAD request per file; when the information is unknown,
/// the file is kept (same as <see cref="LinkFilter"/>).
/// </summary>
public sealed class CrawlOptions
{
    public required string StartUrl { get; init; }

    /// <summary>0 means only the start page. Hard limit 10.</summary>
    public int MaxDepth { get; init; } = 1;

    /// <summary>Maximum number of HTML pages fetched. Hard limit 10,000.</summary>
    public int MaxPages { get; init; } = 50;

    /// <summary>Limit on the total number of file candidates collected.</summary>
    public int MaxCandidates { get; init; } = 5000;

    public CrawlHostScope HostScope { get; init; } = CrawlHostScope.ExactHost;

    /// <summary>Regex on candidate file URLs. Only matching files are kept. Null means all.</summary>
    public string? IncludeRegex { get; init; }

    /// <summary>Regex on page and file URLs. Matches are dropped.</summary>
    public string? ExcludeRegex { get; init; }

    /// <summary>Allowed file extensions (no dot, lowercase). Null means all.</summary>
    public IReadOnlySet<string>? Extensions { get; init; }

    public long? MinSizeBytes { get; init; }
    public long? MaxSizeBytes { get; init; }

    /// <summary>Only files modified after this date (when Last-Modified is known).</summary>
    public DateTimeOffset? ModifiedAfter { get; init; }

    /// <summary>Only files modified before this date (when Last-Modified is known).</summary>
    public DateTimeOffset? ModifiedBefore { get; init; }

    /// <summary>If true (default), robots.txt is respected for every request.</summary>
    public bool RespectRobotsTxt { get; init; } = true;

    /// <summary>Delay between two requests (politeness). robots.txt Crawl-delay is used when larger.</summary>
    public TimeSpan PoliteDelay { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>One candidate file found during the crawl.</summary>
public sealed record CrawlCandidate(string Url, string Extension, long? SizeBytes, DateTimeOffset? LastModifiedUtc, int Depth, string FoundOn);

/// <summary>One fetched page.</summary>
public sealed record CrawledPage(string Url, int Depth, int Status, int LinksFound);

/// <summary>Current progress, reported after each page and candidate.</summary>
public sealed record CrawlProgress(int PagesVisited, int PagesQueued, int CandidatesFound, string CurrentUrl);

/// <summary>Crawl session result. If canceled, partial results are returned and <see cref="Cancelled"/> is true.</summary>
public sealed record CrawlResult(
    IReadOnlyList<CrawlCandidate> Candidates,
    IReadOnlyList<CrawledPage> Pages,
    int SkippedByRobots,
    int SkippedByScope,
    int Errors,
    bool Cancelled);

/// <summary>
/// Recursive link crawler (grabber). One session at a time; requests are made sequentially
/// with a polite delay. HTML pages are fetched and their links extracted via <see cref="PageTools"/>.
/// No page scripts are run. GET for pages and HEAD for file information only.
/// </summary>
public sealed class SiteCrawler : IDisposable
{
    public const int MaxDepthLimit = 10;
    public const int MaxPagesLimit = 10_000;
    public const int MaxPageBytes = 2 * 1024 * 1024;
    public const int MaxRobotsBytes = 512 * 1024;

    internal const string UserAgent = "WinIDM-Grabber/1.0";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly PageTools _tools;
    private int _running;

    public SiteCrawler(HttpClient? httpClient = null)
    {
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _tools = new PageTools(_http);
    }

    /// <summary>True if a session is running.</summary>
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>
    /// Run a crawl session. Throws <see cref="InvalidOperationException"/> if another session is running.
    /// </summary>
    public async Task<CrawlResult> RunAsync(CrawlOptions options, IProgress<CrawlProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("Another grabber session is running.");
        try
        {
            var session = new CrawlSession(this, options, progress);
            return await session.RunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>Check whether a host is in scope. Used by the session and tests.</summary>
    internal static bool IsInScope(string startHost, string candidateHost, CrawlHostScope scope)
    {
        string start = HostUtil.NormalizeHost(startHost);
        string host = HostUtil.NormalizeHost(candidateHost);
        switch (scope)
        {
            case CrawlHostScope.AnyHost:
                return true;
            case CrawlHostScope.ExactHost:
                return host == start;
            case CrawlHostScope.IncludeSubdomains:
                if (host == start) return true;
                string registrable = HostUtil.RegistrableDomain(start);
                return host == registrable || host.EndsWith("." + registrable, StringComparison.Ordinal);
            default:
                return false;
        }
    }

    internal static string Extension(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        string path = uri.AbsolutePath;
        int slash = path.LastIndexOf('/');
        string name = slash >= 0 ? path[(slash + 1)..] : path;
        int dot = name.LastIndexOf('.');
        return dot < 0 ? "" : name[(dot + 1)..].ToLowerInvariant();
    }

    internal static string StripFragment(Uri uri) => new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;

    private static void ValidateOptions(CrawlOptions o)
    {
        if (!HostUtil.TryGetHttpUri(o.StartUrl, out _)) throw new ArgumentException("Start URL must be http(s).", nameof(o));
        if (o.MaxDepth < 0 || o.MaxDepth > MaxDepthLimit) throw new ArgumentOutOfRangeException(nameof(o), $"Depth must be 0 to {MaxDepthLimit}.");
        if (o.MaxPages < 1 || o.MaxPages > MaxPagesLimit) throw new ArgumentOutOfRangeException(nameof(o), $"Page count must be 1 to {MaxPagesLimit}.");
        if (o.MaxCandidates < 1) throw new ArgumentOutOfRangeException(nameof(o), "Candidate limit must be at least 1.");
        if (o.PoliteDelay < TimeSpan.Zero || o.PoliteDelay > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(o), "Polite delay must be 0 to 60 seconds.");
        if (o.MinSizeBytes is long min && o.MaxSizeBytes is long max && min > max)
            throw new ArgumentException("Minimum size cannot exceed maximum size.", nameof(o));
        if (o.ModifiedAfter is { } after && o.ModifiedBefore is { } before && after > before)
            throw new ArgumentException("The 'after' date cannot be later than the 'before' date.", nameof(o));
        CompileFilter(o.IncludeRegex, nameof(o.IncludeRegex));
        CompileFilter(o.ExcludeRegex, nameof(o.ExcludeRegex));
    }

    internal static Regex? CompileFilter(string? pattern, string paramName)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("Invalid regex.", paramName, ex);
        }
    }

    internal static bool Matches(Regex regex, string url)
    {
        try { return regex.IsMatch(url); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    /// <summary>State for one session. Recreated for each <see cref="RunAsync"/>.</summary>
    private sealed class CrawlSession
    {
        private readonly SiteCrawler _owner;
        private readonly CrawlOptions _o;
        private readonly IProgress<CrawlProgress>? _progress;
        private readonly string _startHost;
        private readonly Regex? _include;
        private readonly Regex? _exclude;
        private readonly HashSet<string> _extensions;
        private readonly Dictionary<string, RobotsTxt> _robots = new(StringComparer.Ordinal);
        private readonly HashSet<string> _visited = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seenCandidates = new(StringComparer.Ordinal);
        private readonly List<CrawlCandidate> _candidates = new();
        private readonly List<CrawledPage> _pages = new();
        private readonly Queue<(string Url, int Depth)> _queue = new();
        private bool _anyRequest;
        private int _skippedRobots;
        private int _skippedScope;
        private int _errors;

        public CrawlSession(SiteCrawler owner, CrawlOptions options, IProgress<CrawlProgress>? progress)
        {
            _owner = owner;
            _o = options;
            _progress = progress;
            var start = new Uri(options.StartUrl, UriKind.Absolute);
            _startHost = HostUtil.NormalizeHost(start.Host);
            _include = CompileFilter(options.IncludeRegex, nameof(options.IncludeRegex));
            _exclude = CompileFilter(options.ExcludeRegex, nameof(options.ExcludeRegex));
            _extensions = options.Extensions is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : options.Extensions.Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        }

        public async Task<CrawlResult> RunAsync(CancellationToken ct)
        {
            string startUrl = StripFragment(new Uri(_o.StartUrl, UriKind.Absolute));
            _queue.Enqueue((startUrl, 0));
            _visited.Add(startUrl);

            bool cancelled = false;
            try
            {
                while (_queue.Count > 0 && _pages.Count < _o.MaxPages && _candidates.Count < _o.MaxCandidates)
                {
                    ct.ThrowIfCancellationRequested();
                    var (url, depth) = _queue.Dequeue();
                    await VisitAsync(url, depth, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                cancelled = true;
            }

            return new CrawlResult(_candidates.ToArray(), _pages.ToArray(), _skippedRobots, _skippedScope, _errors, cancelled);
        }

        private async Task VisitAsync(string url, int depth, CancellationToken ct)
        {
            var uri = new Uri(url, UriKind.Absolute);
            if (!await AllowedByRobotsAsync(uri, ct).ConfigureAwait(false))
            {
                _skippedRobots++;
                return;
            }

            await PaceAsync(uri, ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "text/html, application/xhtml+xml;q=0.9, */*;q=0.5");

            HttpResponseMessage response;
            try
            {
                response = await _owner._http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _errors++;
                _pages.Add(new CrawledPage(url, depth, 0, 0));
                return;
            }

            using (response)
            {
                int status = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    _errors++;
                    _pages.Add(new CrawledPage(url, depth, status, 0));
                    return;
                }

                string finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? url;
                string? mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is not ("text/html" or "application/xhtml+xml"))
                {
                    // The server returned the file directly; it is a candidate, not a page.
                    _pages.Add(new CrawledPage(url, depth, status, 0));
                    await TryAddCandidateAsync(url, depth, url, response.Content.Headers.ContentLength,
                        response.Content.Headers.LastModified, ct).ConfigureAwait(false);
                    return;
                }

                string html = await ReadTextLimitedAsync(response, ct).ConfigureAwait(false);
                var links = _owner._tools.ExtractLinks(html, finalUrl);
                _pages.Add(new CrawledPage(url, depth, status, links.Count));
                Report(url);

                foreach (var link in links)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!HostUtil.TryGetHttpUri(link.Url, out var linkUri)) continue;
                    if (!InScope(linkUri.Host)) { _skippedScope++; continue; }

                    if (link.Category == FileCategory.Page)
                    {
                        if (depth + 1 > _o.MaxDepth) continue;
                        if (_exclude is not null && Matches(_exclude, link.Url)) continue;
                        if (_visited.Add(link.Url)) _queue.Enqueue((link.Url, depth + 1));
                    }
                    else
                    {
                        if (_candidates.Count >= _o.MaxCandidates) break;
                        await TryAddCandidateAsync(link.Url, depth, url, null, null, ct, probe: true).ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task TryAddCandidateAsync(string url, int depth, string foundOn, long? knownSize,
            DateTimeOffset? knownModified, CancellationToken ct, bool probe = false)
        {
            string ext = Extension(url);
            if (_extensions.Count > 0 && !_extensions.Contains(ext)) return;
            if (_include is not null && !Matches(_include, url)) return;
            if (_exclude is not null && Matches(_exclude, url)) return;
            if (!_seenCandidates.Add(url)) return;

            long? size = knownSize;
            DateTimeOffset? modified = knownModified;
            bool needsInfo = _o.MinSizeBytes is not null || _o.MaxSizeBytes is not null ||
                             _o.ModifiedAfter is not null || _o.ModifiedBefore is not null;
            if (probe && needsInfo)
            {
                (size, modified) = await HeadAsync(url, ct).ConfigureAwait(false);
            }

            if (size is long s)
            {
                if (_o.MinSizeBytes is long min && s < min) return;
                if (_o.MaxSizeBytes is long max && s > max) return;
            }
            if (modified is DateTimeOffset m)
            {
                if (_o.ModifiedAfter is { } after && m < after) return;
                if (_o.ModifiedBefore is { } before && m > before) return;
            }

            _candidates.Add(new CrawlCandidate(url, ext, size, modified, depth, foundOn));
            Report(url);
        }

        private async Task<(long? Size, DateTimeOffset? Modified)> HeadAsync(string url, CancellationToken ct)
        {
            var uri = new Uri(url, UriKind.Absolute);
            if (!await AllowedByRobotsAsync(uri, ct).ConfigureAwait(false)) return (null, null);
            await PaceAsync(uri, ct).ConfigureAwait(false);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, uri);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                using var response = await _owner._http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return (null, null);
                return (response.Content.Headers.ContentLength, response.Content.Headers.LastModified);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return (null, null);
            }
        }

        private bool InScope(string host) => IsInScope(_startHost, host, _o.HostScope);

        private void Report(string currentUrl) =>
            _progress?.Report(new CrawlProgress(_pages.Count, _queue.Count, _candidates.Count, currentUrl));

        // ---------------------------------------------------------------------
        // Robots and pacing

        private async Task<bool> AllowedByRobotsAsync(Uri uri, CancellationToken ct)
        {
            if (!_o.RespectRobotsTxt) return true;
            var robots = await GetRobotsAsync(uri, ct).ConfigureAwait(false);
            return robots.IsAllowed(uri.PathAndQuery);
        }

        private async Task<RobotsTxt> GetRobotsAsync(Uri uri, CancellationToken ct)
        {
            string origin = uri.Scheme + "://" + uri.Authority;
            if (_robots.TryGetValue(origin, out var cached)) return cached;

            RobotsTxt result;
            await PaceAsync(uri, ct, robotsFetch: true).ConfigureAwait(false);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin + "/robots.txt"));
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                using var response = await _owner._http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    string text = await ReadTextLimitedAsync(response, ct, MaxRobotsBytes).ConfigureAwait(false);
                    result = RobotsTxt.Parse(text);
                }
                else if (status >= 400 && status < 500)
                {
                    // Not available: everything is allowed (RFC 9309).
                    result = RobotsTxt.AllowAll;
                }
                else
                {
                    // Server error: treat as unreachable; deny everything to be safe.
                    result = RobotsTxt.DisallowAll;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException && !ct.IsCancellationRequested)
            {
                result = RobotsTxt.DisallowAll;
            }

            _robots[origin] = result;
            return result;
        }

        private async Task PaceAsync(Uri uri, CancellationToken ct, bool robotsFetch = false)
        {
            if (!_anyRequest)
            {
                _anyRequest = true;
                return;
            }
            TimeSpan delay = _o.PoliteDelay;
            if (!robotsFetch && _robots.TryGetValue(uri.Scheme + "://" + uri.Authority, out var r) &&
                r.CrawlDelay is TimeSpan crawl && crawl > delay)
            {
                delay = crawl;
            }
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        private static async Task<string> ReadTextLimitedAsync(HttpResponseMessage response, CancellationToken ct, int maxBytes = MaxPageBytes)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                int take = (int)Math.Min(read, maxBytes - ms.Length);
                if (take <= 0) break;
                ms.Write(buffer, 0, take);
                if (ms.Length >= maxBytes) break;
            }
            return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
