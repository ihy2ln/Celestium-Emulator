using System.Diagnostics;

namespace DroidLauncher;

/// <summary>
/// Sends Android key events (Back, Recents, Enter, arrows…) through one long-lived "adb shell" per device, so each key
/// costs ~0.1 s instead of starting adb every time. The emulator console can't do this: its raw key events don't reach
/// Android on current images, and the shell isn't allowed to write to the input devices directly.
/// </summary>
sealed class KeyInjector : IDisposable
{
    static readonly Dictionary<string, KeyInjector> Open = new();
    readonly string _adb, _serial;
    Process? _shell;
    readonly object _lock = new();

    KeyInjector(string adb, string serial) { _adb = adb; _serial = serial; }

    public static KeyInjector For(string adb, string serial)
    {
        lock (Open)
        {
            if (!Open.TryGetValue(serial, out var k)) Open[serial] = k = new KeyInjector(adb, serial);
            return k;
        }
    }

    public static void Forget(string serial)
    {
        lock (Open) if (Open.Remove(serial, out var k)) k.Dispose();
    }

    /// <summary>Android KeyEvent keycodes, e.g. 4 = BACK, 3 = HOME, 187 = APP_SWITCH, 66 = ENTER, 67 = DEL.</summary>
    public void Press(params int[] keycodes)
    {
        if (keycodes.Length == 0) return;
        lock (_lock)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (_shell is not { HasExited: false }) Start();
                    _shell!.StandardInput.WriteLine("input keyevent " + string.Join(' ', keycodes));
                    _shell.StandardInput.Flush();
                    return;
                }
                catch { _shell?.Dispose(); _shell = null; } // device restarted: open a new shell once
            }
        }
    }

    void Start()
    {
        _shell = Process.Start(new ProcessStartInfo(_adb, $"-s {_serial} shell")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        })!;
        _shell.OutputDataReceived += (_, _) => { }; // drain so the pipe never fills
        _shell.ErrorDataReceived += (_, _) => { };
        _shell.BeginOutputReadLine();
        _shell.BeginErrorReadLine();
    }

    public void Dispose()
    {
        try { if (_shell is { HasExited: false }) { _shell.StandardInput.WriteLine("exit"); if (!_shell.WaitForExit(500)) _shell.Kill(); } } catch { }
        _shell?.Dispose();
        _shell = null;
    }
}
