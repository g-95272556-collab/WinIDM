namespace WinIDM.Backup;

/// <summary>Backup destination in a local folder (or a mounted network drive).</summary>
public sealed class LocalFolderDestination : IBackupDestination
{
    private readonly string _folder;

    public LocalFolderDestination(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(_folder);
    }

    public string DisplayName => $"Local folder: {_folder}";

    public async Task UploadAsync(string localPath, string remoteName, CancellationToken ct = default)
    {
        BackupNames.Validate(remoteName);
        await using var source = File.OpenRead(localPath);
        await DestinationIo.SaveStreamAtomicAsync(source, Path.Combine(_folder, remoteName), ct);
    }

    public Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<BackupEntry> entries = Directory
            .EnumerateFiles(_folder, BackupNames.Prefix + "*")
            .Select(p => new FileInfo(p))
            .Where(f => BackupNames.IsSafe(f.Name))
            .Select(f => new BackupEntry(f.Name, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero), f.Length))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(entries);
    }

    public async Task DownloadAsync(string remoteName, string localPath, CancellationToken ct = default)
    {
        BackupNames.Validate(remoteName);
        var source = Path.Combine(_folder, remoteName);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Backup file not found at the destination.", source);
        }

        await using var input = File.OpenRead(source);
        await DestinationIo.SaveStreamAtomicAsync(input, localPath, ct);
    }

    public Task DeleteAsync(string remoteName, CancellationToken ct = default)
    {
        BackupNames.Validate(remoteName);
        ct.ThrowIfCancellationRequested();
        var path = Path.Combine(_folder, remoteName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }
}
