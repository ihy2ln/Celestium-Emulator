using System.Net.Sockets;
using System.Text;

namespace DroidLauncher;

/// <summary>
/// Persistent connection to an emulator's console (telnet on its port). Much faster than spawning
/// "adb emu ..." per command, which matters for controls, snapshots, recording and, later, touch input.
/// </summary>
sealed class EmulatorConsole : IDisposable
{
    readonly int _port;
    TcpClient? _client;
    StreamReader? _reader;
    StreamWriter? _writer;
    readonly SemaphoreSlim _gate = new(1, 1);

    EmulatorConsole(int port) => _port = port;

    static readonly Dictionary<int, EmulatorConsole> Open = new();

    /// <summary>Shared connection per console port (one per running device).</summary>
    public static EmulatorConsole For(string serial)
    {
        var port = int.Parse(serial["emulator-".Length..]);
        lock (Open)
        {
            if (!Open.TryGetValue(port, out var c)) Open[port] = c = new EmulatorConsole(port);
            return c;
        }
    }

    public static void Forget(string serial)
    {
        if (!int.TryParse(serial["emulator-".Length..], out var port)) return;
        lock (Open)
            if (Open.Remove(port, out var c)) c.Dispose();
    }

    async Task ConnectAsync()
    {
        _client?.Dispose();
        _client = new TcpClient { NoDelay = true };
        using var cts = new CancellationTokenSource(3000);
        await _client.ConnectAsync("127.0.0.1", _port, cts.Token);
        var stream = _client.GetStream();
        _reader = new StreamReader(stream, Encoding.ASCII);
        _writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        await ReadUntilOk(); // banner
        var tokenFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".emulator_console_auth_token");
        if (File.Exists(tokenFile))
        {
            await _writer.WriteLineAsync("auth " + File.ReadAllText(tokenFile).Trim());
            await ReadUntilOk();
        }
    }

    async Task<string> ReadUntilOk()
    {
        var sb = new StringBuilder();
        using var cts = new CancellationTokenSource(10_000);
        while (true)
        {
            var line = await _reader!.ReadLineAsync(cts.Token) ?? throw new IOException("Console closed.");
            if (line == "OK") return sb.ToString().TrimEnd();
            if (line.StartsWith("KO")) throw new InvalidOperationException(line.Length > 3 ? line[3..].Trim() : "Command failed.");
            sb.AppendLine(line);
        }
    }

    /// <summary>Runs one console command and returns its output (text before the final OK).</summary>
    public async Task<string> Send(string command)
    {
        await _gate.WaitAsync();
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (_client is not { Connected: true }) await ConnectAsync();
                    await _writer!.WriteLineAsync(command);
                    return await ReadUntilOk();
                }
                catch (Exception) when (attempt == 0)
                {
                    _client?.Dispose(); _client = null; // stale connection (device restarted): reconnect once
                }
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Like Send, but swallows errors; for fire-and-forget controls.</summary>
    public async Task<bool> TrySend(string command)
    {
        try { await Send(command); return true; } catch { return false; }
    }

    public void Dispose()
    {
        try { _writer?.WriteLine("quit"); } catch { }
        _client?.Dispose();
        _gate.Dispose();
    }
}
