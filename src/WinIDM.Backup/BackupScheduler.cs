using WinIDM.Core.Contracts;
using WinIDM.Core.Models;

namespace WinIDM.Backup;

public enum BackupTrigger { Manual, Interval, AppOpen, AppClose }

/// <summary>Backup scheduler settings (persisted by App).</summary>
public sealed class BackupScheduleOptions
{
    /// <summary>Default categories: everything except Passwords and Saved pages.</summary>
    public const BackupCategories DefaultCategories =
        BackupCategories.Settings | BackupCategories.Downloads | BackupCategories.Bookmarks
        | BackupCategories.History | BackupCategories.DomainLimits | BackupCategories.Adblock
        | BackupCategories.UserScripts;

    public bool Enabled { get; set; }

    /// <summary>Interval between periodic backups in hours. 0 = not periodic.</summary>
    public int IntervalHours { get; set; }

    public bool BackupOnAppOpen { get; set; }

    public bool BackupOnAppClose { get; set; }

    /// <summary>Number of backup files kept at the destination. 0 = keep all.</summary>
    public int RetentionCount { get; set; }

    public BackupCategories Categories { get; set; } = DefaultCategories;

    public string? Password { get; set; }

    public void Validate()
    {
        if (IntervalHours < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(IntervalHours), "The interval cannot be negative.");
        }

        if (RetentionCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RetentionCount), "Retention cannot be negative.");
        }

        if (Categories == BackupCategories.None)
        {
            throw new ArgumentException("Select at least one category.", nameof(Categories));
        }

        if (!string.IsNullOrEmpty(Password))
        {
            BackupCrypto.ValidatePassword(Password);
        }
    }
}

/// <param name="Trigger">Cause of the backup.</param>
/// <param name="StartedUtc">Start time (according to <see cref="ISystemClock"/>).</param>
/// <param name="Success">True if the backup was created and uploaded.</param>
/// <param name="RemoteName">File name at the destination (if successful).</param>
/// <param name="DeletedByRetention">Number of old backups deleted by retention.</param>
/// <param name="Error">Error message if it failed.</param>
/// <param name="Warning">Non-critical warning (for example, retention failed even though the backup succeeded).</param>
public sealed record BackupRunResult(
    BackupTrigger Trigger,
    DateTimeOffset StartedUtc,
    bool Success,
    string? RemoteName,
    int DeletedByRetention,
    string? Error,
    string? Warning = null);

/// <summary>Retention policy: select old backup names that should be deleted.</summary>
public static class RetentionPolicy
{
    /// <summary>Keep the <paramref name="keep"/> newest backups. If 0, nothing is deleted.</summary>
    public static IReadOnlyList<string> SelectForDeletion(IEnumerable<string> names, int keep)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (keep <= 0)
        {
            return Array.Empty<string>();
        }

        return names
            .Where(n => n.StartsWith(BackupNames.Prefix, StringComparison.Ordinal))
            .OrderByDescending(n => n, StringComparer.Ordinal)
            .Skip(keep)
            .ToList();
    }
}

/// <summary>
/// Backup scheduler: periodic (every N hours), app open and close, plus manual backups.
/// Time is read from <see cref="ISystemClock"/> so it can be tested with a fake clock.
/// <see cref="TickAsync"/> is the periodic checkpoint; <see cref="RunLoopAsync"/> calls it every minute.
/// </summary>
public sealed class BackupScheduler
{
    private readonly IBackupService _backup;
    private readonly IBackupDestination _destination;
    private readonly BackupScheduleOptions _options;
    private readonly ISystemClock _clock;
    private readonly string _stagingDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTimeOffset? _nextIntervalUtc;

    public BackupScheduler(
        IBackupService backup,
        IBackupDestination destination,
        BackupScheduleOptions options,
        ISystemClock clock,
        string stagingDirectory)
    {
        _backup = backup ?? throw new ArgumentNullException(nameof(backup));
        _destination = destination ?? throw new ArgumentNullException(nameof(destination));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        _options.Validate();
        _stagingDirectory = stagingDirectory;
    }

    /// <summary>Next periodic backup time (UTC), or null if not scheduled.</summary>
    public DateTimeOffset? NextIntervalRunUtc => _nextIntervalUtc;

    /// <summary>Raised before each backup starts.</summary>
    public event Action<BackupTrigger>? Triggered;

    /// <summary>Raised after each backup finishes (success or failure).</summary>
    public event Action<BackupRunResult>? RunCompleted;

    /// <summary>Start the periodic schedule: first backup <c>IntervalHours</c> from now.</summary>
    public void Start()
    {
        _nextIntervalUtc = _options.Enabled && _options.IntervalHours > 0
            ? _clock.UtcNow.AddHours(_options.IntervalHours)
            : null;
    }

    public void Stop() => _nextIntervalUtc = null;

    /// <summary>Run a periodic backup if it is due. Returns null if it is not due yet.</summary>
    public async Task<BackupRunResult?> TickAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled || _nextIntervalUtc is null || _clock.UtcNow < _nextIntervalUtc.Value)
        {
            return null;
        }

        _nextIntervalUtc = _clock.UtcNow.AddHours(_options.IntervalHours);
        return await RunAsync(BackupTrigger.Interval, ct);
    }

    /// <summary>Called after the app opens. Backs up only if enabled in settings.</summary>
    public Task<BackupRunResult?> OnAppOpenedAsync(CancellationToken ct = default) =>
        FireIfAllowedAsync(BackupTrigger.AppOpen, _options.BackupOnAppOpen, ct);

    /// <summary>Called while the app is closing. Backs up only if enabled in settings.</summary>
    public Task<BackupRunResult?> OnAppClosingAsync(CancellationToken ct = default) =>
        FireIfAllowedAsync(BackupTrigger.AppClose, _options.BackupOnAppClose, ct);

    /// <summary>Manual backup; runs even when the scheduler is disabled.</summary>
    public Task<BackupRunResult> RunNowAsync(CancellationToken ct = default) =>
        RunAsync(BackupTrigger.Manual, ct);

    /// <summary>Check loop every minute until <paramref name="ct"/> is canceled.</summary>
    public async Task RunLoopAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            await _clock.DelayAsync(TimeSpan.FromMinutes(1), ct);
        }
    }

    private async Task<BackupRunResult?> FireIfAllowedAsync(BackupTrigger trigger, bool allowed, CancellationToken ct)
    {
        if (!_options.Enabled || !allowed)
        {
            return null;
        }

        return await RunAsync(trigger, ct);
    }

    private async Task<BackupRunResult> RunAsync(BackupTrigger trigger, CancellationToken ct)
    {
        Triggered?.Invoke(trigger);
        var started = _clock.UtcNow;

        await _gate.WaitAsync(ct);
        string? staging = null;
        try
        {
            Directory.CreateDirectory(_stagingDirectory);
            var encrypted = !string.IsNullOrEmpty(_options.Password);
            var name = BackupNames.NewName(started, encrypted);
            staging = Path.Combine(_stagingDirectory, $"{Guid.NewGuid():N}.tmp");

            await _backup.CreateAsync(staging, _options.Categories, _options.Password, ct);
            await _destination.UploadAsync(staging, name, ct);

            var (deleted, warning) = await ApplyRetentionAsync(ct);
            return Complete(new BackupRunResult(trigger, started, true, name, deleted, null, warning));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Complete(new BackupRunResult(trigger, started, false, null, 0, ex.Message));
        }
        finally
        {
            if (staging is not null)
            {
                TryDelete(staging);
            }

            _gate.Release();
        }
    }

    private async Task<(int Deleted, string? Warning)> ApplyRetentionAsync(CancellationToken ct)
    {
        if (_options.RetentionCount <= 0)
        {
            return (0, null);
        }

        try
        {
            var entries = await _destination.ListAsync(ct);
            var toDelete = RetentionPolicy.SelectForDeletion(entries.Select(e => e.Name), _options.RetentionCount);
            var deleted = 0;
            foreach (var name in toDelete)
            {
                await _destination.DeleteAsync(name, ct);
                deleted++;
            }

            return (deleted, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (0, $"Backup succeeded, but retention failed: {ex.Message}");
        }
    }

    private BackupRunResult Complete(BackupRunResult result)
    {
        RunCompleted?.Invoke(result);
        return result;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Temporary file; not critical.
        }
        catch (UnauthorizedAccessException)
        {
            // Temporary file; not critical.
        }
    }
}
