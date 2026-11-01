namespace WinIDM.Backup;

/// <summary>File I/O utilities for destinations: write to a temporary file first, then replace.</summary>
internal static class DestinationIo
{
    public static Task SaveStreamAtomicAsync(Stream source, string localPath, CancellationToken ct) =>
        SaveStreamAtomicAsync(source, localPath, null, -1, ct);

    public static async Task SaveStreamAtomicAsync(
        Stream source, string localPath, IProgress<TransferProgress>? progress, long totalBytes, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(localPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var partPath = fullPath + ".part";
        try
        {
            await using (var destination = File.Create(partPath))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report(new TransferProgress(done, totalBytes));
                }
            }

            File.Move(partPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(partPath))
            {
                File.Delete(partPath);
            }
        }
    }

    /// <summary>Read an exact byte range from a file (for upload chunks).</summary>
    public static byte[] ReadRange(string path, long start, int length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }
}

/// <summary>Read stream that reports upload progress while HttpClient copies the content.</summary>
internal sealed class ProgressReadStream : Stream
{
    private readonly Stream _inner;
    private readonly IProgress<TransferProgress>? _progress;
    private readonly long _total;
    private long _done;

    public ProgressReadStream(Stream inner, IProgress<TransferProgress>? progress, long total)
    {
        _inner = inner;
        _progress = progress;
        _total = total;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _done;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        Advance(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        Advance(read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Advance(int read)
    {
        if (read > 0)
        {
            _done += read;
            _progress?.Report(new TransferProgress(_done, _total));
        }
    }
}
