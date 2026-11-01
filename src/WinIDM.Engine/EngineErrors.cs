namespace WinIDM.Engine;

/// <summary>
/// Download error codes. Codes 108, 118, 130, 416, 429 and 509 follow IDM+ (01 section 7.3).
/// Other HTTP errors use the HTTP status code itself (for example 404, 503).
/// </summary>
public static class EngineErrorCodes
{
    public const int UnknownError = 1;
    public const int InvalidLength = 108;
    public const int IoError = 118;
    public const int ConnectionError = 130;
    public const int RangeNotSatisfiable = 416;
    public const int TooManyRequests = 429;
    public const int BandwidthExceeded = 509;

    /// <summary>WinIDM code: SHA-256/MD5 checksum does not match after the download.</summary>
    public const int ChecksumMismatch = 180;

    /// <summary>
    /// Formerly: SOCKS proxy not yet supported. No longer emitted since SOCKS5 support (see the engine README).
    /// The value is kept so code referring to it still compiles.
    /// </summary>
    public const int UnsupportedProxy = 181;

    /// <summary>WinIDM code: invalid proxy configuration (port outside 1-65535, empty host).</summary>
    public const int InvalidProxy = 182;

    /// <summary>WinIDM code: too many redirects.</summary>
    public const int TooManyRedirects = 183;

    /// <summary>WinIDM code: playlist (m3u8) format not supported or invalid.</summary>
    public const int UnsupportedPlaylist = 184;

    /// <summary>WinIDM code: invalid download address.</summary>
    public const int InvalidUrl = 185;

    /// <summary>
    /// WinIDM code: the server does not support ranges and the transfer was interrupted, so it cannot continue.
    /// Retried from zero only when <see cref="SettingKeys.AutoRestartNonResumable"/> is on (IDM+ behaviour).
    /// </summary>
    public const int NotResumable = 186;
}

/// <summary>Transfer error with a code and a flag for whether it can be retried.</summary>
internal sealed class TransferException : Exception
{
    public TransferException(int code, string message, bool retryable, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Retryable = retryable;
        RetryAfter = retryAfter;
    }

    public int Code { get; }

    public bool Retryable { get; }

    /// <summary>Server-suggested delay (Retry-After header), if any.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// Signals that the download must restart from scratch (the file changed on the server,
/// or the server ignored the range during a partial download).
/// </summary>
internal sealed class RestartRequiredException : Exception
{
    public RestartRequiredException(string reason) : base(reason)
    {
    }
}
