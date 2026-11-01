using WinIDM.Backup;

namespace WinIDM.Backup.Tests;

public class SchedulerTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static BackupScheduleOptions Options(bool enabled = true, int hours = 6, int retention = 0) => new()
    {
        Enabled = enabled,
        IntervalHours = hours,
        RetentionCount = retention,
    };

    [Fact]
    public async Task Interval_backup_runs_only_when_due_using_fake_clock()
    {
        using var dir = new TempDir();
        var backup = new CountingBackupService();
        var destination = new LocalFolderDestination(dir.File("dest"));
        var clock = new FakeClock(Start);
        var scheduler = new BackupScheduler(backup, destination, Options(hours: 6), clock, dir.File("stage"));

        scheduler.Start();
        Assert.Equal(Start.AddHours(6), scheduler.NextIntervalRunUtc);

        clock.Advance(TimeSpan.FromHours(5));
        Assert.Null(await scheduler.TickAsync());
        Assert.Equal(0, backup.CreateCount);

        clock.Advance(TimeSpan.FromHours(1));
        var result = await scheduler.TickAsync();

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal(BackupTrigger.Interval, result.Trigger);
        Assert.Equal(1, backup.CreateCount);
        Assert.Equal(clock.UtcNow.AddHours(6), scheduler.NextIntervalRunUtc);

        Assert.Null(await scheduler.TickAsync());
        Assert.Equal(1, backup.CreateCount);
    }

    [Fact]
    public async Task Disabled_scheduler_never_runs_interval_backup()
    {
        using var dir = new TempDir();
        var backup = new CountingBackupService();
        var clock = new FakeClock(Start);
        var scheduler = new BackupScheduler(backup, new LocalFolderDestination(dir.File("dest")),
            Options(enabled: false, hours: 1), clock, dir.File("stage"));

        scheduler.Start();
        clock.Advance(TimeSpan.FromDays(3));

        Assert.Null(await scheduler.TickAsync());
        Assert.Equal(0, backup.CreateCount);
        Assert.Null(scheduler.NextIntervalRunUtc);
    }

    [Fact]
    public async Task Retention_keeps_only_newest_n_backups()
    {
        using var dir = new TempDir();
        var backup = new CountingBackupService();
        var destination = new LocalFolderDestination(dir.File("dest"));
        var clock = new FakeClock(Start);
        var scheduler = new BackupScheduler(backup, destination, Options(retention: 2), clock, dir.File("stage"));

        var results = new List<BackupRunResult>();
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            results.Add(await scheduler.RunNowAsync());
        }

        var remaining = (await destination.ListAsync()).Select(e => e.Name).ToList();
        Assert.Equal(2, remaining.Count);
        Assert.Equal(new[] { results[4].RemoteName, results[3].RemoteName }.OrderBy(n => n, StringComparer.Ordinal), remaining.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(3, results[4].DeletedByRetention + results[3].DeletedByRetention + results[2].DeletedByRetention);
    }

    [Fact]
    public async Task Retention_zero_keeps_every_backup()
    {
        using var dir = new TempDir();
        var destination = new LocalFolderDestination(dir.File("dest"));
        var clock = new FakeClock(Start);
        var scheduler = new BackupScheduler(new CountingBackupService(), destination, Options(retention: 0), clock, dir.File("stage"));

        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True((await scheduler.RunNowAsync()).Success);
        }

        Assert.Equal(3, (await destination.ListAsync()).Count);
    }

    [Fact]
    public async Task App_open_and_close_triggers_run_only_when_configured()
    {
        using var dir = new TempDir();
        var backup = new CountingBackupService();
        var options = Options(hours: 0);
        options.BackupOnAppOpen = true;
        options.BackupOnAppClose = false;
        var scheduler = new BackupScheduler(backup, new LocalFolderDestination(dir.File("dest")),
            options, new FakeClock(Start), dir.File("stage"));

        var fired = new List<BackupTrigger>();
        scheduler.Triggered += fired.Add;

        var opened = await scheduler.OnAppOpenedAsync();
        var closed = await scheduler.OnAppClosingAsync();

        Assert.NotNull(opened);
        Assert.Equal(BackupTrigger.AppOpen, opened!.Trigger);
        Assert.Null(closed);
        Assert.Equal(new[] { BackupTrigger.AppOpen }, fired);
        Assert.Equal(1, backup.CreateCount);
    }

    [Fact]
    public async Task Failed_backup_is_reported_in_result_and_event_without_throwing()
    {
        using var dir = new TempDir();
        var backup = new CountingBackupService { ThrowOnCreate = new InvalidOperationException("disk full") };
        var scheduler = new BackupScheduler(backup, new LocalFolderDestination(dir.File("dest")),
            Options(hours: 0), new FakeClock(Start), dir.File("stage"));

        BackupRunResult? completed = null;
        scheduler.RunCompleted += r => completed = r;

        var result = await scheduler.RunNowAsync();

        Assert.False(result.Success);
        Assert.Equal("disk full", result.Error);
        Assert.Same(result, completed);
    }

    [Fact]
    public async Task Manual_run_works_even_when_scheduler_disabled()
    {
        using var dir = new TempDir();
        var backup = new CountingBackupService();
        var scheduler = new BackupScheduler(backup, new LocalFolderDestination(dir.File("dest")),
            Options(enabled: false, hours: 0), new FakeClock(Start), dir.File("stage"));

        var result = await scheduler.RunNowAsync();

        Assert.True(result.Success);
        Assert.Equal(BackupTrigger.Manual, result.Trigger);
    }

    [Fact]
    public void Retention_policy_selects_oldest_beyond_keep_and_ignores_foreign_files()
    {
        var names = new[]
        {
            "winidm-backup-20260101-000000-000.zip",
            "winidm-backup-20260103-000000-000.zip",
            "winidm-backup-20260102-000000-000.zip",
            "my-notes.txt",
        };

        var toDelete = RetentionPolicy.SelectForDeletion(names, keep: 1);

        Assert.Equal(new[]
        {
            "winidm-backup-20260102-000000-000.zip",
            "winidm-backup-20260101-000000-000.zip",
        }, toDelete);
        Assert.Empty(RetentionPolicy.SelectForDeletion(names, keep: 0));
    }

    [Fact]
    public void Invalid_options_are_refused()
    {
        using var dir = new TempDir();
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackupScheduler(new CountingBackupService(),
            new LocalFolderDestination(dir.File("d")), Options(hours: -1), new FakeClock(Start), dir.File("s")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackupScheduler(new CountingBackupService(),
            new LocalFolderDestination(dir.File("d")), Options(retention: -1), new FakeClock(Start), dir.File("s")));
    }
}
