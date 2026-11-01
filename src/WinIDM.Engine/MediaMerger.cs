using System.Diagnostics;

namespace WinIDM.Engine;

/// <summary>
/// Media merger and converter built on external ffmpeg. ffmpeg is not bundled; it is found on PATH.
/// Arguments are built as a list (no shell), so file names with spaces are safe.
/// </summary>
public sealed class MediaMerger
{
    private readonly string? _ffmpegPath;

    /// <param name="ffmpegPath">Explicit ffmpeg path; null means search PATH.</param>
    public MediaMerger(string? ffmpegPath = null)
    {
        _ffmpegPath = ffmpegPath ?? FfmpegLocator.Find();
    }

    /// <summary>True if ffmpeg is available.</summary>
    public bool IsAvailable => _ffmpegPath is not null;

    /// <summary>ffmpeg path in use, or null if none.</summary>
    public string? FfmpegPath => _ffmpegPath;

    /// <summary>Arguments to merge video and audio without transcoding (stream copy).</summary>
    public static IReadOnlyList<string> BuildMergeArguments(string videoPath, string audioPath, string outputPath) =>
    [
        "-y",
        "-i", videoPath,
        "-i", audioPath,
        "-map", "0:v:0",
        "-map", "1:a:0",
        "-c", "copy",
        "-movflags", "+faststart",
        outputPath,
    ];

    /// <summary>Arguments to convert .ts to .mp4 without transcoding (stream copy).</summary>
    public static IReadOnlyList<string> BuildConvertArguments(string inputPath, string outputPath) =>
    [
        "-y",
        "-i", inputPath,
        "-c", "copy",
        "-movflags", "+faststart",
        outputPath,
    ];

    /// <summary>Merges the video and audio files into <paramref name="outputPath"/>.</summary>
    public Task MergeAsync(string videoPath, string audioPath, string outputPath, CancellationToken ct = default) =>
        RunAsync(BuildMergeArguments(videoPath, audioPath, outputPath), ct);

    /// <summary>Converts a .ts file to .mp4.</summary>
    public Task ConvertTsToMp4Async(string inputPath, string outputPath, CancellationToken ct = default) =>
        RunAsync(BuildConvertArguments(inputPath, outputPath), ct);

    private async Task RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        if (_ffmpegPath is null)
        {
            throw new InvalidOperationException("ffmpeg not found on PATH. Install ffmpeg to merge or convert media.");
        }

        var startInfo = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process already exited.
            }
        });

        await process.WaitForExitAsync(ct);
        await stdout;
        var error = await stderr;

        if (process.ExitCode != 0)
        {
            var tail = error.Length > 500 ? error[^500..] : error;
            throw new InvalidOperationException($"ffmpeg failed (code {process.ExitCode}): {tail.Trim()}");
        }
    }
}

/// <summary>Finds ffmpeg on PATH without bundling it.</summary>
internal static class FfmpegLocator
{
    public static string? Find()
    {
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(entry.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Invalid PATH entries are skipped.
            }
        }

        return null;
    }
}
