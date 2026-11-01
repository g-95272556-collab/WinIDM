using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace WinIDM.App.Services;

/// <summary>
/// One instance only. A second instance sends the command line arguments (URL, magnet, .torrent) through a
/// named pipe to the running instance, then exits. The pipe accepts only a single JSON line
/// of a string array and sends nothing back.
/// </summary>
public static class SingleInstance
{
    public const string PipeName = "WinIDM-SingleInstance-v1";

    /// <summary>Try to send the arguments to an existing instance. Returns true if another instance received them.</summary>
    public static bool TrySendToRunning(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 500);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(args));
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Pipe listener on the first instance. Events are raised on a background thread; the caller must dispatch to the UI.</summary>
public sealed class SingleInstanceServer : IDisposable
{
    private const int MaxPayloadChars = 64 * 1024;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public event Action<string[]>? ArgumentsReceived;

    public void Start() => _loop = Task.Run(() => ListenAsync(_cts.Token));

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    SingleInstance.PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await ReadBoundedLineAsync(reader, ct);
                if (line is null) continue;
                var args = JsonSerializer.Deserialize<string[]>(line);
                if (args is not null) ArgumentsReceived?.Invoke(args);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException)
            {
                AppLog.Error("Single instance pipe", ex);
            }
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[MaxPayloadChars];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(count, 1), ct);
            if (read == 0) break;
            if (buffer[count] == '\n') return new string(buffer, 0, count).TrimEnd('\r');
            count++;
        }
        return count == 0 ? null : new string(buffer, 0, count);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
