using WinIDM.Backup;
using WinIDM.Core.Models;

namespace WinIDM.App.Services;

/// <summary>
/// Wires <see cref="BackupScheduler"/> to app settings. The scheduler is rebuilt when backup settings
/// change. Scheduled backups MUST be encrypted: without a scheduled backup password the scheduler is disabled
/// and the status says why (no unencrypted backup is ever made silently).
/// </summary>
public sealed class BackupAutomation : IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

    private readonly AppSettings _settings;
    private readonly BackupDestinations _destinations;
    private readonly AppBackupService _backup;
    private readonly string _stagingDirectory;
    private readonly object _gate = new();
    private BackupScheduler? _scheduler;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public BackupAutomation(AppSettings settings, BackupDestinations destinations, AppBackupService backup, string stagingDirectory)
    {
        _settings = settings;
        _destinations = destinations;
        _backup = backup;
        _stagingDirectory = stagingDirectory;
        _settings.Changed += OnSettingChanged;
    }

    /// <summary>The most recent scheduled or open/close backup result.</summary>
    public BackupRunResult? LastResult { get; private set; }

    /// <summary>Status message to display (for example, why the scheduler is inactive).</summary>
    public string StatusText { get; private set; } = "";

    public event Action<BackupRunResult>? Completed;

    /// <summary>Rebuild the scheduler from the current settings.</summary>
    public void Reload()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _scheduler?.Stop();
            _scheduler = null;

            var options = BuildOptions(out var problem);
            StatusText = problem ?? "";
            if (options is null) return;

            var destination = new LazyBackupDestination(() =>
                _destinations.Create(BackupDestinations.KindFromChoice(_settings.Text(BackupKeys.Destination))));
            var scheduler = new BackupScheduler(_backup, destination, options, SystemClock.Instance, _stagingDirectory);
            scheduler.RunCompleted += OnRunCompleted;
            scheduler.Start();
            _scheduler = scheduler;
        }
    }

    /// <summary>Start the once-a-minute check loop. Called once when the app starts.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null || _disposed) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(token));
        }
    }

    /// <summary>Backup on app open (only when enabled in settings).</summary>
    public Task<BackupRunResult?> OnAppOpenedAsync(CancellationToken ct = default) =>
        CurrentOrNull() is { } s ? s.OnAppOpenedAsync(ct) : Task.FromResult<BackupRunResult?>(null);

    /// <summary>Backup before the app closes (only when enabled in settings).</summary>
    public Task<BackupRunResult?> OnAppClosingAsync(CancellationToken ct = default) =>
        CurrentOrNull() is { } s ? s.OnAppClosingAsync(ct) : Task.FromResult<BackupRunResult?>(null);

    private BackupScheduler? CurrentOrNull()
    {
        lock (_gate) return _scheduler;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var s = CurrentOrNull();
                if (s is not null) await s.TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLog.Error("Backup scheduler", ex);
            }

            try
            {
                await Task.Delay(CheckEvery, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private BackupScheduleOptions? BuildOptions(out string? problem)
    {
        problem = null;
        var password = _settings.Text(BackupKeys.SchedulePassword);
        var options = new BackupScheduleOptions
        {
            Enabled = _settings.Bool(BackupKeys.ScheduleEnabled),
            IntervalHours = _settings.Int(BackupKeys.ScheduleInterval),
            BackupOnAppOpen = _settings.Bool(BackupKeys.ScheduleOnOpen),
            BackupOnAppClose = _settings.Bool(BackupKeys.ScheduleOnClose),
            RetentionCount = _settings.Int(BackupKeys.ScheduleRetention),
            Categories = (BackupCategories)_settings.Int(BackupKeys.ScheduleCategories),
            Password = string.IsNullOrEmpty(password) ? null : password,
        };

        if (!options.Enabled && !options.BackupOnAppOpen && !options.BackupOnAppClose)
        {
            return null;
        }

        if (string.IsNullOrEmpty(options.Password))
        {
            problem = "Scheduler disabled: set a scheduled backup password first (scheduled backups must be encrypted).";
            return null;
        }

        try
        {
            if (options.Categories.HasFlag(BackupCategories.Passwords) && options.Password.Length < BackupKeys.MinPasswordCategoryLength)
            {
                problem = $"The Passwords category requires a backup password of at least {BackupKeys.MinPasswordCategoryLength} characters.";
                return null;
            }

            options.Validate();
            return options;
        }
        catch (ArgumentException ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    private void OnRunCompleted(BackupRunResult result)
    {
        LastResult = result;
        var text = result.Success
            ? $"Succeeded {DateTime.Now:yyyy-MM-dd HH:mm}: {result.RemoteName}"
            : $"Failed {DateTime.Now:yyyy-MM-dd HH:mm}: {result.Error}";
        _settings.Set(BackupKeys.LastStatus, text);
        Completed?.Invoke(result);
    }

    private void OnSettingChanged(string key)
    {
        if (key.StartsWith("backup.schedule", StringComparison.Ordinal) ||
            key is BackupKeys.Destination or BackupKeys.LocalFolder or BackupKeys.RestUrl)
        {
            Reload();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _settings.Changed -= OnSettingChanged;
            _scheduler?.Stop();
            _cts?.Cancel();
        }
    }
}
