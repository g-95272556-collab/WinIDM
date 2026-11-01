namespace WinIDM.Backup;

/// <summary>
/// Retry policy for transient errors (429, 5xx, network interruptions).
/// Delays grow exponentially and are capped by <see cref="MaxDelay"/>; <c>Retry-After</c> is honored up to <see cref="MaxRetryAfter"/>.
/// </summary>
public sealed class RetryPolicy
{
    public static RetryPolicy Standard { get; } = new();

    /// <summary>Maximum number of attempts, including the first one.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Base delay before the first retry.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Cap on the exponential delay.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Cap for the Retry-After value returned by the server.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromMinutes(2);

    public void Validate()
    {
        if (MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), "At least one attempt is required.");
        }

        if (BaseDelay < TimeSpan.Zero || MaxDelay < BaseDelay || MaxRetryAfter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(BaseDelay), "Invalid delay value.");
        }
    }

    /// <summary>Delay before the next attempt after <paramref name="failures"/> failures (starting at 1).</summary>
    public TimeSpan ExponentialDelay(int failures)
    {
        var exponent = Math.Min(Math.Max(failures - 1, 0), 30);
        var ticks = BaseDelay.Ticks * Math.Pow(2, exponent);
        return TimeSpan.FromTicks((long)Math.Min(ticks, MaxDelay.Ticks));
    }
}

/// <summary>Shared settings for cloud destinations: retry policy and time source (testable with a fake clock).</summary>
public sealed class CloudOptions
{
    public RetryPolicy Retry { get; init; } = RetryPolicy.Standard;

    public ISystemClock Clock { get; init; } = SystemClock.Instance;

    public void Validate() => Retry.Validate();
}

/// <summary>Transfer progress. <see cref="TotalBytes"/> is -1 when unknown.</summary>
public readonly record struct TransferProgress(long BytesTransferred, long TotalBytes)
{
    public bool HasTotal => TotalBytes >= 0;
}

/// <summary>Destination that reports upload and download progress.</summary>
public interface IProgressBackupDestination : IBackupDestination
{
    Task UploadAsync(string localPath, string remoteName, IProgress<TransferProgress>? progress, CancellationToken ct = default);

    Task DownloadAsync(string remoteName, string localPath, IProgress<TransferProgress>? progress, CancellationToken ct = default);
}
