using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WinIDM.Backup;

/// <summary>
/// HTTP helper for cloud destinations: inject a Bearer token, refresh once after a 401,
/// retry with backoff for 429/5xx and network interruptions, and interpret errors.
/// The request is rebuilt on every attempt (<paramref name="build"/>), so the content can be read again.
/// Tokens are never included in error messages or logs.
/// </summary>
internal static class CloudHttp
{
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        IOAuthTokenProvider auth,
        CloudOptions options,
        Func<HttpRequestMessage> build,
        CancellationToken ct,
        HttpCompletionOption option = HttpCompletionOption.ResponseContentRead)
    {
        var policy = options.Retry;
        var token = await auth.GetAccessTokenAsync(ct);
        var refreshed = false;
        var failures = 0;

        while (true)
        {
            HttpResponseMessage response;
            try
            {
                var request = build();
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                response = await http.SendAsync(request, option, ct);
            }
            catch (Exception ex) when (IsTransportFailure(ex, ct))
            {
                failures++;
                if (failures >= policy.MaxAttempts)
                {
                    throw new BackupTransportException("Cannot reach the cloud service.", ex);
                }

                await options.Clock.DelayAsync(policy.ExponentialDelay(failures), ct);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
            {
                response.Dispose();
                refreshed = true;
                token = await auth.RefreshAccessTokenAsync(ct);
                continue;
            }

            if (IsTransient(response.StatusCode) && failures + 1 < policy.MaxAttempts)
            {
                failures++;
                var delay = RetryDelay(response, failures, policy);
                response.Dispose();
                await options.Clock.DelayAsync(delay, ct);
                continue;
            }

            return response;
        }
    }

    public static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    public static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                $"The {operation} operation failed: permission denied (HTTP 401). Reconnect the account.",
            HttpStatusCode.Forbidden =>
                $"The {operation} operation failed: access denied (HTTP 403). Check the account's permission scopes.",
            _ => $"The {operation} operation failed (HTTP {status}).",
        };
        throw new BackupTransportException(message, status);
    }

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new BackupTransportException("The cloud service response is not valid JSON.", ex);
        }
    }

    private static bool IsTransportFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException
        || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    private static TimeSpan RetryDelay(HttpResponseMessage response, int failures, RetryPolicy policy)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return policy.ExponentialDelay(failures);
        }

        var delay = retryAfter.Delta
            ?? (retryAfter.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.Zero);
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        return delay > policy.MaxRetryAfter ? policy.MaxRetryAfter : delay;
    }
}
