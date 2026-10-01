using System.Diagnostics;
using System.Text.Json;

namespace DroidLauncher;

record Config(string SdkRoot);

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(LoadConfig()));
    }

    static Config LoadConfig()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "launcher.json");
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? new Config(@"S:\Android");
        }
        catch { }
        return new Config(Environment.GetEnvironmentVariable("ANDROID_HOME") ?? @"S:\Android");
    }
}

/// <summary>Thin wrapper over emulator.exe and adb.exe.</summary>
class Sdk(Config cfg)
{
    public string Emulator => Path.Combine(cfg.SdkRoot, "emulator", "emulator.exe");
    public string Adb => Path.Combine(cfg.SdkRoot, "platform-tools", "adb.exe");

    public static async Task<(int code, string output)> Run(string exe, string args, int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-1, "timed out"); }
        return (p.ExitCode, (await outTask) + (await errTask));
    }

    public async Task<List<string>> ListAvds()
    {
        var (_, output) = await Run(Emulator, "-list-avds", 30_000);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("INFO") && !l.StartsWith("WARNING") && !l.Contains(' '))
            .ToList();
    }

    /// <summary>Maps running AVD name → (serial, state).</summary>
    public async Task<Dictionary<string, (string serial, string state)>> RunningAvds()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var (_, output) = await Run(Adb, "devices", 15_000);
        foreach (var line in output.Split('\n').Skip(1))
        {
            var parts = line.Split('\t', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !parts[0].StartsWith("emulator-")) continue;
            var (_, name) = await Run(Adb, $"-s {parts[0]} emu avd name", 10_000);
            var avd = name.Split('\n', StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            if (avd.Length > 0 && !avd.StartsWith("error", StringComparison.OrdinalIgnoreCase)) map[avd] = (parts[0], parts[1]);
        }
        return map;
    }

    public void Start(string avd, string extraArgs)
    {
        Process.Start(new ProcessStartInfo(Emulator, $"-avd {avd} {extraArgs}".Trim())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    public Task Stop(string serial) => Run(Adb, $"-s {serial} emu kill", 15_000);

    public async Task<bool> IsBooted(string serial)
    {
        var (_, o) = await Run(Adb, $"-s {serial} shell getprop sys.boot_completed", 10_000);
        return o.Trim() == "1";
    }

    public Task<(int, string)> Install(string serial, string apk) =>
        Run(Adb, $"-s {serial} install -r -g \"{apk}\"", 600_000);

    public void Logcat(string serial) =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k title logcat {serial} && \"{Adb}\" -s {serial} logcat -v color")
        { UseShellExecute = true });

    public void Shell(string serial) =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k title shell {serial} && \"{Adb}\" -s {serial} shell")
        { UseShellExecute = true });
}
