using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using WinIDM.Core.Models;

namespace WinIDM.Engine;

/// <summary>Shared HTTP helpers: request headers, manual redirects, and error classification.</summary>
internal static class HttpTransfer
{
    public const string DefaultUserAgent = "WinIDM/1.0";
    public const int MaxRedirects = 8;

    private static readonly HashSet<int> RedirectStatus = [300, 301, 302, 303, 307, 308];

    /// <summary>Applies the usual download headers: UA, Accept, Accept-Encoding identity, referer, cookies, auth and extra headers.</summary>
    public static void ApplyItemHeaders(HttpRequestMessage request, DownloadItem item, string? userAgentSetting)
    {
        // Encoding must be identity so range offsets match the file bytes.
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

        // The value "null" means drop the User-Agent header (01 section 6.4).
        var userAgent = item.UserAgent ?? userAgentSetting;
        if (!string.Equals(userAgent, "null", StringComparison.Ordinal))
        {
            request.Headers.TryAddWithoutValidation("User-Agent",
                string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent);
        }

        if (!string.IsNullOrWhiteSpace(item.Referer))
        {
            request.Headers.TryAddWithoutValidation("Referer", item.Referer);
        }

        // Cookies and credentials belong to the site the user added; a redirect (or HLS segment) on another site
        // must not receive them. Same site = same host, or the same registrable domain (www./cdn. of one site).
        var credentialsAllowed = IsSameSite(request.RequestUri, item.Url);

        if (credentialsAllowed && !string.IsNullOrWhiteSpace(item.CookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", item.CookieHeader);
        }

        if (credentialsAllowed && !string.IsNullOrEmpty(item.Username))
        {
            var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{item.Username}:{item.Password ?? string.Empty}"));
            request.Headers.TryAddWithoutValidation("Authorization", "Basic " + token);
        }

        foreach (var (name, value) in item.Headers)
        {
            // Range and condition headers are controlled by the engine only.
            if (name.Equals("Range", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("If-Range", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!credentialsAllowed &&
                (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    /// <summary>
    /// True when <paramref name="target"/> belongs to the same site as <paramref name="originalUrl"/>: the same host,
    /// or the same registrable domain (last two labels, three for a two-letter country code under a short second
    /// level such as co.uk or com.my). IP addresses must match exactly. An unknown target is treated as same site.
    /// </summary>
    public static bool IsSameSite(Uri? target, string? originalUrl)
    {
        if (target is null || !Uri.TryCreate(originalUrl, UriKind.Absolute, out var original)) return true;
        if (string.Equals(target.Host, original.Host, StringComparison.OrdinalIgnoreCase)) return true;
        if (target.HostNameType != UriHostNameType.Dns || original.HostNameType != UriHostNameType.Dns) return false;
        return string.Equals(RegistrableDomain(target.Host), RegistrableDomain(original.Host), StringComparison.OrdinalIgnoreCase);
    }

    private static string RegistrableDomain(string host)
    {
        var labels = host.TrimEnd('.').Split('.');
        if (labels.Length <= 2) return string.Join('.', labels);
        bool countrySecondLevel = labels[^1].Length == 2 && labels[^2].Length <= 3;
        int take = countrySecondLevel ? 3 : 2;
        return string.Join('.', labels[^take..]);
    }

    /// <summary>
    /// Sends a GET request and follows redirects (300/301/302/303/307/308) manually.
    /// Returns the final response and the final URL.
    /// </summary>
    public static async Task<(HttpResponseMessage Response, Uri FinalUri)> SendAsync(
        HttpClient client,
        Uri start,
        Action<HttpRequestMessage> decorate,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var uri = start;
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            decorate(request);

            HttpResponseMessage response;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) linked.CancelAfter(timeout);
                try
                {
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TransferException(EngineErrorCodes.ConnectionError, "Timed out connecting to the server.", retryable: true);
                }
                catch (HttpRequestException ex)
                {
                    throw new TransferException(EngineErrorCodes.ConnectionError, "Connection to the server failed: " + ex.Message, retryable: true, inner: ex);
                }
                catch (IOException ex)
                {
                    throw new TransferException(EngineErrorCodes.ConnectionError, "Network error: " + ex.Message, retryable: true, inner: ex);
                }
            }

            if (!RedirectStatus.Contains((int)response.StatusCode) || response.Headers.Location is null)
            {
                return (response, uri);
            }

            response.Dispose();
            if (hop >= MaxRedirects)
            {
                throw new TransferException(EngineErrorCodes.TooManyRedirects, "Too many redirects.", retryable: false);
            }

            uri = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(uri, response.Headers.Location);
        }
    }

    /// <summary>Translates an HTTP status code into a transfer exception (or null on success).</summary>
    public static TransferException? ClassifyStatus(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status is >= 200 and < 300) return null;

        var retryAfter = ParseRetryAfter(response);
        var message = $"Server replied {status} {response.ReasonPhrase}".Trim();

        return status switch
        {
            (int)HttpStatusCode.RequestTimeout => new TransferException(status, message, retryable: true, retryAfter),
            (int)HttpStatusCode.TooManyRequests => new TransferException(EngineErrorCodes.TooManyRequests, message, retryable: true, retryAfter),
            (int)HttpStatusCode.RequestedRangeNotSatisfiable => new TransferException(EngineErrorCodes.RangeNotSatisfiable, message, retryable: false),
            >= 500 and < 600 when status != 501 && status != 505 => new TransferException(status, message, retryable: true, retryAfter),
            _ => new TransferException(status, message, retryable: false),
        };
    }

    /// <summary>Reads Retry-After as seconds (numeric value) or an HTTP date.</summary>
    public static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var value = response.Headers.RetryAfter;
        if (value is null) return null;
        if (value.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (value.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    /// <summary>Parses the Content-Range "bytes a-b/total" from the response; null if missing or invalid.</summary>
    public static ContentRangeHeaderValue? ParseContentRange(HttpResponseMessage response)
    {
        var range = response.Content.Headers.ContentRange;
        return range is { From: not null, To: not null } ? range : null;
    }

    /// <summary>Validator for If-Range: strong ETag (not W/) or Last-Modified; null if none.</summary>
    public static RangeConditionHeaderValue? BuildIfRange(string? etag, string? lastModified)
    {
        if (!string.IsNullOrEmpty(etag) && !etag.StartsWith("W/", StringComparison.Ordinal))
        {
            if (EntityTagHeaderValue.TryParse(etag, out var entityTag))
            {
                return new RangeConditionHeaderValue(entityTag);
            }
        }

        if (!string.IsNullOrEmpty(lastModified) && DateTimeOffset.TryParse(lastModified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return new RangeConditionHeaderValue(date);
        }

        return null;
    }

    /// <summary>Converts a Last-Modified date to an RFC 1123 string for storage.</summary>
    public static string? FormatLastModified(DateTimeOffset? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Translates a network/IO exception into a retryable error.</summary>
    public static TransferException FromNetworkException(Exception ex) =>
        new(EngineErrorCodes.ConnectionError, "Network error: " + ex.Message, retryable: true, inner: ex);
}
