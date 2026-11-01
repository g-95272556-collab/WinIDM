using WinIDM.Backup;
using WinIDM.Core.Contracts;
using WinIDM.Core.Models;

namespace WinIDM.App.Services;

/// <summary>
/// Backup password context for a single operation. <see cref="BackupDataSource"/> reads this value
/// to encrypt the password vault export; it is never written to disk.
/// </summary>
public static class BackupPasswordContext
{
    private static readonly AsyncLocal<string?> Slot = new();

    public static string? Current => Slot.Value;

    public static IDisposable Enter(string? password)
    {
        var previous = Slot.Value;
        Slot.Value = password;
        return new Restorer(previous);
    }

    private sealed class Restorer : IDisposable
    {
        private readonly string? _previous;

        public Restorer(string? previous) => _previous = previous;

        public void Dispose() => Slot.Value = _previous;
    }
}

/// <summary>
/// Wraps <see cref="BackupService"/> in the App. Supplies the password context for extra data sources
/// and enforces the "minimum 12-character password" rule for the Passwords category before anything is written.
/// </summary>
public sealed class AppBackupService : IBackupService
{
    private readonly BackupService _inner;

    public AppBackupService(BackupService inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public async Task CreateAsync(string destinationPath, BackupCategories categories, string? password, CancellationToken ct = default)
    {
        EnsurePasswordRequirement(categories, password);
        using var _ = BackupPasswordContext.Enter(password);
        await _inner.CreateAsync(destinationPath, categories, password, ct);
    }

    public async Task RestoreAsync(string sourcePath, BackupCategories categories, string? password, CancellationToken ct = default)
    {
        using var _ = BackupPasswordContext.Enter(password);
        await _inner.RestoreAsync(sourcePath, categories, password, ct);
    }

    /// <summary>Restore with a detailed report (Merge or Overwrite mode).</summary>
    public async Task<RestoreReport> RestoreWithReportAsync(
        string sourcePath, BackupCategories categories, string? password, RestoreMode mode, CancellationToken ct = default)
    {
        using var _ = BackupPasswordContext.Enter(password);
        return await _inner.RestoreWithReportAsync(sourcePath, categories, password, mode, ct);
    }

    internal static void EnsurePasswordRequirement(BackupCategories categories, string? password)
    {
        if (categories.HasFlag(BackupCategories.Passwords) &&
            (password is null || password.Length < BackupKeys.MinPasswordCategoryLength))
        {
            throw new ArgumentException(
                $"The Passwords category requires a backup password of at least {BackupKeys.MinPasswordCategoryLength} characters.",
                nameof(password));
        }
    }
}
