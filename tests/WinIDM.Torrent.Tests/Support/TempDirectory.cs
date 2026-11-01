namespace WinIDM.Torrent.Tests.Support;

/// <summary>A unique temporary folder deleted after the test.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WinIDM.Torrent.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // The file may still be locked by the engine; do not fail the test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
