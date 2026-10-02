using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DroidLauncher;

static class AppInfo
{
    /// <summary>The real app version. The exe's file version stays 1.0.0 so the exe never changes (see the csproj).</summary>
    public const string Version = "1.6.0";
}

record Config(string SdkRoot)
{
    /// <summary>launcher.json next to the exe, else ANDROID_HOME, else S:\Android.</summary>
    public static Config Load()
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

static class Paths
{
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CelestiumEmulator");
    public static string AvdHome => Environment.GetEnvironmentVariable("ANDROID_AVD_HOME") is { Length: > 0 } h
        ? h
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "avd");

    /// <summary>Write via temp file + rename so a crash or a second copy reading mid-write never sees half a file.</summary>
    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, true);
    }

    public static T ReadJson<T>(string path) where T : new()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T() : new T(); }
            catch (IOException) { Thread.Sleep(50); }
            catch (JsonException) { break; }
        }
        return new T();
    }
}

/// <summary>Per-window UI state, kept in %APPDATA%\CelestiumEmulator\state.json.</summary>
class AppState
{
    public int X { get; set; } = int.MinValue;
    public int Y { get; set; }
    public int Width { get; set; } = 620;
    public int Height { get; set; } = 520;
    public bool Maximized { get; set; }
    public bool StartHidden { get; set; }
    public bool RestoreInstances { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public string? LastApkFolder { get; set; }
    public List<string> RunningInstances { get; set; } = new();
    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; set; } = "system";
    /// <summary>Where screenshots and recordings go; empty = Pictures\Celestium.</summary>
    public string? MediaFolder { get; set; }
    public string? SelectedAvd { get; set; }
    public DateTime? LastUpdateCheck { get; set; }

    static string FilePath => Path.Combine(Paths.DataDir, "state.json");
    public static AppState Load() => Paths.ReadJson<AppState>(FilePath);
    public void Save() { try { Paths.WriteJson(FilePath, this); } catch { } }
}

/// <summary>What Celestium knows about one Android instance beyond the AVD itself.</summary>
class InstanceInfo
{
    /// <summary>Console port. The adb serial is always emulator-{Port}, so tools can rely on it.</summary>
    public int Port { get; set; }
    /// <summary>Free-text label, e.g. the project this device is for ("Weaverse", "Adams Haven").</summary>
    public string? Project { get; set; }
    /// <summary>Guest RAM cap in MB, passed as -memory. Null means the AVD's own hw.ramSize.</summary>
    public int? RamMb { get; set; }
    /// <summary>Run with -no-window: no screen, so less RAM and GPU. For devices that tools drive over adb.</summary>
    public bool Headless { get; set; }
    /// <summary>Performance preset name (Low/Balanced/High/Ultra), or null if RAM/cores were set by hand.</summary>
    public string? Preset { get; set; }
    /// <summary>Virtual CPU cores (-cores); null = emulator default.</summary>
    public int? Cores { get; set; }
    /// <summary>"emulated" (virtual scene), "webcam0" (PC webcam) or "none".</summary>
    public string? CameraBack { get; set; }
    public string? CameraFront { get; set; }
    /// <summary>Pass the PC microphone through (-allow-host-audio). Off by default for privacy.</summary>
    public bool HostMic { get; set; }
    /// <summary>Device profile it was created with, for display.</summary>
    public string? Profile { get; set; }
}

/// <summary>
/// Shared registry of instance ports and project labels (%APPDATA%\CelestiumEmulator\instances.json).
/// Read fresh on every use because several app windows and the CLI may all change it.
/// </summary>
static class InstanceStore
{
    const int FirstPort = 5554, LastPort = 5680; // adb only auto-discovers emulators in this range
    static string FilePath => Path.Combine(Paths.DataDir, "instances.json");
    static readonly Mutex Lock = new(false, "CelestiumEmulator.InstanceStore");

    public static Dictionary<string, InstanceInfo> Load() =>
        new(Paths.ReadJson<Dictionary<string, InstanceInfo>>(FilePath), StringComparer.OrdinalIgnoreCase);

    static T Update<T>(Func<Dictionary<string, InstanceInfo>, T> change)
    {
        try { Lock.WaitOne(5000); } catch (AbandonedMutexException) { }
        try
        {
            var all = Load();
            var result = change(all);
            Paths.WriteJson(FilePath, all);
            return result;
        }
        finally { try { Lock.ReleaseMutex(); } catch { } }
    }

    public static InstanceInfo Get(string avd) => Load().TryGetValue(avd, out var i) ? i : new InstanceInfo();

    /// <summary>
    /// The instance's fixed port, assigning a free one the first time. Called only when the instance is
    /// stopped, so if something else has grabbed its port meanwhile, it moves to a free one.
    /// </summary>
    public static int EnsurePort(string avd) => Update(all =>
    {
        if (!all.TryGetValue(avd, out var info)) all[avd] = info = new InstanceInfo();
        var listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        if (info.Port != 0 && !listening.Contains(info.Port) && !listening.Contains(info.Port + 1)) return info.Port;
        info.Port = 0;
        var taken = all.Values.Select(i => i.Port).ToHashSet();
        for (int p = FirstPort; p <= LastPort; p += 2)
            if (!taken.Contains(p) && !listening.Contains(p) && !listening.Contains(p + 1))
                return info.Port = p;
        throw new InvalidOperationException("No free emulator ports left (5554-5681).");
    });

    public static void SetProject(string avd, string? project) => Update(all =>
    {
        if (!all.TryGetValue(avd, out var info)) all[avd] = info = new InstanceInfo();
        info.Project = string.IsNullOrWhiteSpace(project) ? null : project.Trim();
        return 0;
    });

    public static void SetRam(string avd, int? ramMb) => Update(all =>
    {
        if (!all.TryGetValue(avd, out var info)) all[avd] = info = new InstanceInfo();
        info.RamMb = ramMb;
        return 0;
    });

    /// <summary>Change any settings of one instance atomically (other windows/CLI may be editing too).</summary>
    public static void Edit(string avd, Action<InstanceInfo> change) => Update(all =>
    {
        if (!all.TryGetValue(avd, out var info)) all[avd] = info = new InstanceInfo();
        change(info);
        return 0;
    });

    public static void SetHeadless(string avd, bool headless) => Update(all =>
    {
        if (!all.TryGetValue(avd, out var info)) all[avd] = info = new InstanceInfo();
        info.Headless = headless;
        return 0;
    });

    public static void Remove(string avd) => Update(all => all.Remove(avd));

    /// <summary>Find an instance by AVD name or project label (case-insensitive).</summary>
    public static string? Resolve(string nameOrProject, IEnumerable<string> avds)
    {
        var list = avds.ToList();
        var exact = list.FirstOrDefault(a => a.Equals(nameOrProject, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var all = Load();
        return list.FirstOrDefault(a => all.TryGetValue(a, out var i) &&
                                         string.Equals(i.Project, nameOrProject, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Creates new AVDs by copying an existing one's hardware profile and system image (not its apps or data).</summary>
static class AvdFactory
{
    public static readonly Regex ValidName = new("^[A-Za-z0-9._-]{1,40}$");

    /// <summary>Set (or add) key=value lines in an AVD's config.ini. Takes effect on the next cold boot.</summary>
    public static void SetConfig(string name, IDictionary<string, string> values)
    {
        var path = Path.Combine(Paths.AvdHome, name + ".avd", "config.ini");
        var lines = File.ReadAllLines(path).ToList();
        foreach (var (key, value) in values)
        {
            var i = lines.FindIndex(l => l.Split('=', 2)[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) lines[i] = $"{key}={value}"; else lines.Add($"{key}={value}");
        }
        File.WriteAllLines(path, lines);
    }

    /// <summary>Give an AVD a device profile's screen. The old quick-boot snapshot no longer fits, so it cold boots once.</summary>
    public static void ApplyProfile(string name, DeviceProfile profile)
    {
        SetConfig(name, new Dictionary<string, string>
        {
            ["hw.lcd.width"] = profile.Width.ToString(),
            ["hw.lcd.height"] = profile.Height.ToString(),
            ["hw.lcd.density"] = profile.Dpi.ToString(),
            ["hw.sensor.hinge"] = profile.Foldable ? "yes" : "no",
            ["showDeviceFrame"] = "no",
        });
        var snapshot = Path.Combine(Paths.AvdHome, name + ".avd", "snapshots", "default_boot");
        try { if (Directory.Exists(snapshot)) Directory.Delete(snapshot, true); } catch { }
        InstanceStore.Edit(name, i => i.Profile = profile.Name);
    }

    public static void CreateFrom(string template, string name, DeviceProfile? profile)
    {
        CreateFrom(template, name);
        if (profile != null) ApplyProfile(name, profile);
    }

    public static void CreateFrom(string template, string name)
    {
        if (!ValidName.IsMatch(name)) throw new ArgumentException("Use letters, numbers, '.', '_' or '-' (no spaces).");
        var home = Paths.AvdHome;
        var srcDir = Path.Combine(home, template + ".avd");
        var dstDir = Path.Combine(home, name + ".avd");
        var dstIni = Path.Combine(home, name + ".ini");
        if (!File.Exists(Path.Combine(srcDir, "config.ini"))) throw new FileNotFoundException($"'{template}' has no config.ini.");
        if (Directory.Exists(dstDir) || File.Exists(dstIni)) throw new InvalidOperationException($"An instance named '{name}' already exists.");

        Directory.CreateDirectory(dstDir);
        var config = File.ReadAllLines(Path.Combine(srcDir, "config.ini"))
            .Where(l => !l.StartsWith("disk.dataPartition.path", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.StartsWith("avd.ini.displayname", StringComparison.OrdinalIgnoreCase) ? "avd.ini.displayname=" + name : l)
            .Select(l => l.StartsWith("AvdId", StringComparison.OrdinalIgnoreCase) ? "AvdId=" + name : l)
            .ToList();
        File.WriteAllLines(Path.Combine(dstDir, "config.ini"), config);

        var srcIni = Path.Combine(home, template + ".ini");
        var target = File.Exists(srcIni)
            ? File.ReadAllLines(srcIni).FirstOrDefault(l => l.StartsWith("target=")) ?? "target=android-35"
            : "target=android-35";
        File.WriteAllLines(dstIni, new[]
        {
            "avd.ini.encoding=UTF-8",
            "path=" + dstDir,
            "path.rel=avd\\" + name + ".avd",
            target,
        });
    }

    /// <summary>Whether the AVD's system image includes the Google Play Store.</summary>
    public static bool HasPlayStore(string name)
    {
        try
        {
            return File.ReadLines(Path.Combine(Paths.AvdHome, name + ".avd", "config.ini"))
                .Any(l => l.Replace(" ", "").Equals("PlayStore.enabled=yes", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>The hw.ramSize from an AVD's config.ini, in MB (0 if unknown).</summary>
    public static int ConfiguredRamMb(string name)
    {
        try
        {
            var line = File.ReadLines(Path.Combine(Paths.AvdHome, name + ".avd", "config.ini"))
                .FirstOrDefault(l => l.StartsWith("hw.ramSize", StringComparison.OrdinalIgnoreCase));
            var value = line?.Split('=', 2)[1].Trim().TrimEnd('M', 'm', 'B', 'b');
            return int.TryParse(value, out var mb) ? mb : 0;
        }
        catch { return 0; }
    }

    /// <summary>Permanently deletes an AVD's folder and .ini. The caller confirms with the user first.</summary>
    public static void Delete(string name)
    {
        var home = Paths.AvdHome;
        var dir = Path.Combine(home, name + ".avd");
        var ini = Path.Combine(home, name + ".ini");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        if (File.Exists(ini)) File.Delete(ini);
        InstanceStore.Remove(name);
    }
}

/// <summary>
/// Starts a long-lived process that inherits none of our handles. With Process.Start(UseShellExecute=false)
/// the emulator inherited the caller's stdout pipe, so anything capturing `celestium start` output (scripts,
/// build tools) hung until the emulator exited.
/// </summary>
static class Detached
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool CreateProcessW(string? app, System.Text.StringBuilder cmdLine, IntPtr procAttr, IntPtr threadAttr,
        bool inheritHandles, uint flags, IntPtr env, string? cwd, ref StartupInfo si, out ProcessInformation pi);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    const uint CreateNoWindow = 0x08000000, DetachedProcess = 0x00000008, CreateNewProcessGroup = 0x00000200;

    public static void Start(string exe, string args)
    {
        var si = new StartupInfo { cb = System.Runtime.InteropServices.Marshal.SizeOf<StartupInfo>() };
        var cmd = new System.Text.StringBuilder($"\"{exe}\" {args}");
        if (!CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow | CreateNewProcessGroup,
                IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out var pi))
            throw new System.ComponentModel.Win32Exception();
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
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

    // serial → AVD name, learned once per serial, so a poll costs one "adb devices" instead of one adb call per device.
    readonly Dictionary<string, string> _serialNames = new();

    /// <summary>Maps running AVD name → (serial, state).</summary>
    public async Task<Dictionary<string, (string serial, string state)>> RunningAvds()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var (_, output) = await Run(Adb, "devices", 15_000);
        var seen = new HashSet<string>();
        Dictionary<string, InstanceInfo>? store = null;
        foreach (var line in output.Split('\n').Skip(1))
        {
            var parts = line.Split('\t', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !parts[0].StartsWith("emulator-")) continue;
            var (serial, state) = (parts[0], parts[1]);
            seen.Add(serial);

            if (!_serialNames.TryGetValue(serial, out var avd))
            {
                if (state == "device")
                {
                    // Ask the emulator itself, which also works for devices started outside Celestium.
                    var (_, name) = await Run(Adb, $"-s {serial} emu avd name", 5_000);
                    avd = name.Split('\n', StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
                    if (avd.Length == 0 || avd.StartsWith("error", StringComparison.OrdinalIgnoreCase)) continue;
                    _serialNames[serial] = avd;
                }
                else
                {
                    // Offline or still booting: don't block on its console, because dead entries take seconds to
                    // time out. Our own instances can be recognized by their fixed port.
                    store ??= InstanceStore.Load();
                    var port = int.TryParse(serial["emulator-".Length..], out var p) ? p : -1;
                    avd = store.FirstOrDefault(kv => kv.Value.Port == port).Key;
                    if (avd == null) continue;
                }
            }
            map[avd] = (serial, state);
        }
        foreach (var gone in _serialNames.Keys.Where(k => !seen.Contains(k)).ToList()) _serialNames.Remove(gone);
        return map;
    }

    /// <summary>Boots an AVD on its fixed port, with its memory and window settings, and returns its serial.</summary>
    public string Start(string avd, string extraArgs)
    {
        var port = InstanceStore.EnsurePort(avd);
        var info = InstanceStore.Get(avd);
        var args = $"-avd {avd} -port {port} -no-boot-anim";
        if (info.RamMb is > 0) args += $" -memory {info.RamMb}";
        if (info.Headless) args += " -no-window";
        if (info.Cores is > 0) args += $" -cores {info.Cores}";
        if (info.CameraBack is { Length: > 0 } back) args += $" -camera-back {back}";
        if (info.CameraFront is { Length: > 0 } front) args += $" -camera-front {front}";
        if (info.HostMic) args += " -allow-host-audio";
        Detached.Start(Emulator, $"{args} {extraArgs}".Trim());
        return "emulator-" + port;
    }

    public Task Stop(string serial) => Run(Adb, $"-s {serial} emu kill", 15_000);

    public async Task<bool> IsBooted(string serial)
    {
        var (_, o) = await Run(Adb, $"-s {serial} shell getprop sys.boot_completed", 10_000);
        return o.Trim() == "1";
    }

    public Task<(int, string)> Install(string serial, string apk) =>
        Run(Adb, $"-s {serial} install -r -g \"{apk}\"", 600_000);

    public void Logcat(string serial) => Launch("cmd.exe", $"/k title logcat {serial} && \"{Adb}\" -s {serial} logcat -v color");
    public void OpenShell(string serial) => Launch("cmd.exe", $"/k title shell {serial} && \"{Adb}\" -s {serial} shell");

    /// <summary>Run a command in the device's shell and return its trimmed output.</summary>
    public async Task<string> ShellCmd(string serial, string command, int timeoutMs = 15_000)
    {
        var (_, output) = await Run(Adb, $"-s {serial} shell {command}", timeoutMs);
        return output.Trim();
    }

    public Task KeyEvent(string serial, int keycode) => ShellCmd(serial, $"input keyevent {keycode}");

    /// <summary>PNG of the current screen, or null if the device isn't ready.</summary>
    public async Task<byte[]?> Screencap(string serial)
    {
        var psi = new ProcessStartInfo(Adb, $"-s {serial} exec-out screencap -p")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
        _ = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(10_000);
        try { await p.WaitForExitAsync(cts.Token); await copy; }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return null; }
        var bytes = ms.ToArray();
        return bytes.Length > 8 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G' ? bytes : null;
    }

    public Task<(int, string)> Push(string serial, string localPath, string remoteDir) =>
        Run(Adb, $"-s {serial} push \"{localPath}\" \"{remoteDir}\"", 600_000);

    /// <summary>The emulator's VM process id, from the lock file it keeps in the AVD folder while running.</summary>
    public static int? QemuPid(string avd)
    {
        try
        {
            var file = Path.Combine(Paths.AvdHome, avd + ".avd", "hardware-qemu.ini.lock", "pid");
            if (!File.Exists(file)) return null;
            // The emulator keeps this file open for writing, so it must be read with full sharing.
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return int.TryParse(reader.ReadToEnd().Trim(), out var pid) ? pid : null;
        }
        catch { return null; }
    }

    /// <summary>Fire-and-forget launch that doesn't keep a Process handle alive.</summary>
    public static void Launch(string exe, string args)
    {
        using var _ = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
    }
}
