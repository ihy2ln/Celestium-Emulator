using DroidLauncher;

// celestium â€” command-line control of Celestium Emulator instances, so any program (build scripts,
// Gradle, Unity, AI agents) working on several projects at once can each claim its own device.
// Instances are addressed by AVD name or project label; each has a fixed adb serial (emulator-<port>).

const string Usage = """
    celestium â€” control Celestium Emulator instances

    Usage:
      celestium list                                Show every instance (name, project, serial, state)
      celestium start <instance> [--wait] [--cold] [--headless] [--memory <MB>] [--force]
                                                    Boot it (no-op if running); --wait blocks until booted.
                                                    --headless = no window (less RAM/GPU), --memory caps guest RAM
      celestium stop <instance>                     Shut it down
      celestium serial <instance>                   Print its adb serial (e.g. emulator-5556)
      celestium install <instance> <file.apk>...    Install APKs (starts and waits for it if needed)
      celestium new <name> --from <instance> [--project <label>]
                                                    Create an instance with the same settings as another
      celestium project <instance> [label]          Set (or clear) its project label
      celestium memory <instance> <MB|default>      Set its RAM cap for future starts (e.g. 2048)
      celestium mem [--free]                        Memory report; --free stops idle build daemons and unloads ComfyUI models

    <instance> is an instance name or a project label.
    Tip: adb, Gradle and most Android tools target the device in ANDROID_SERIAL, e.g. in PowerShell:
      $env:ANDROID_SERIAL = celestium serial Weaverse; ./gradlew installDebug
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return 0; }
if (args[0] is "--version" or "version") { Console.WriteLine(AppInfo.Version); return 0; }

var sdk = new Sdk(Config.Load());
if (!File.Exists(sdk.Emulator)) return Fail($"emulator.exe not found at {sdk.Emulator}. Set SdkRoot in launcher.json.");

var cmd = args[0].ToLowerInvariant();
// Positional arguments, minus --flags and the values that belong to --options.
string[] valueOptions = { "--from", "--project", "--memory" };
var rest = new List<string>();
for (int i = 1; i < args.Length; i++)
{
    if (valueOptions.Contains(args[i], StringComparer.OrdinalIgnoreCase)) { i++; continue; }
    if (!args[i].StartsWith("--")) rest.Add(args[i]);
}
bool Flag(string f) => args.Contains(f, StringComparer.OrdinalIgnoreCase);
string? Option(string f)
{
    var i = Array.FindIndex(args, a => a.Equals(f, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var avds = await sdk.ListAvds();

async Task<string?> Find(string key)
{
    var avd = InstanceStore.Resolve(key, avds);
    if (avd == null) Console.Error.WriteLine($"No instance or project called '{key}'. Run 'celestium list'.");
    return await Task.FromResult(avd);
}

async Task<string> StartAndMaybeWait(string avd, bool wait, bool cold)
{
    var running = await sdk.RunningAvds();
    string serial;
    if (running.TryGetValue(avd, out var r)) serial = r.serial;
    else
    {
        // Same memory limits as the app (shared settings). --force skips them.
        if (!Flag("--force"))
        {
            var store = InstanceStore.Load();
            var info = store.TryGetValue(avd, out var i) ? i : new InstanceInfo();
            int ram = Option("--memory") is { } m && int.TryParse(m, out var mm) ? mm : MemoryGuard.DeviceRamMb(avd, info);
            var others = running.Keys.Select(a => (a, store.TryGetValue(a, out var oi) ? oi : new InstanceInfo()));
            var why = MemoryGuard.Check(avd, ram, others, AppState.Load().MemoryBudgetPercent, out var fits);
            if (why != null)
                throw new InvalidOperationException(why + (fits > 0 ? $"\nIt would fit with --memory {fits}." : "") + "\nUse --force to start it anyway.");
        }
        var extra = cold ? "-no-snapshot-load" : "";
        if (Flag("--headless")) extra += " -no-window";
        if (Option("--memory") is { } mem && int.TryParse(mem, out var mb) && mb >= 1024) extra += $" -memory {mb}";
        serial = sdk.Start(avd, extra.Trim());
    }
    if (!wait) return serial;

    var deadline = DateTime.UtcNow.AddMinutes(5);
    while (DateTime.UtcNow < deadline)
    {
        if (await sdk.IsBooted(serial)) return serial;
        await Task.Delay(2000);
    }
    throw new TimeoutException($"{avd} didn't finish booting within 5 minutes.");
}

try
{
    switch (cmd)
    {
        case "list":
        {
            var running = await sdk.RunningAvds();
            var store = InstanceStore.Load();
            Console.WriteLine($"{"INSTANCE",-20} {"PROJECT",-18} {"SERIAL",-15} STATE");
            foreach (var avd in avds)
            {
                store.TryGetValue(avd, out var info);
                var serial = running.TryGetValue(avd, out var r) ? r.serial : info?.Port > 0 ? $"emulator-{info.Port}" : "-";
                var state = running.ContainsKey(avd) ? (await sdk.IsBooted(r.serial) ? "running" : "booting") : "stopped";
                Console.WriteLine($"{avd,-20} {info?.Project ?? "-",-18} {serial,-15} {state}");
            }
            return 0;
        }
        case "start":
        {
            if (rest.Count < 1) return Fail("Usage: celestium start <instance> [--wait] [--cold]");
            var avd = await Find(rest[0]); if (avd == null) return 2;
            Console.WriteLine(await StartAndMaybeWait(avd, Flag("--wait"), Flag("--cold")));
            return 0;
        }
        case "stop":
        {
            if (rest.Count < 1) return Fail("Usage: celestium stop <instance>");
            var avd = await Find(rest[0]); if (avd == null) return 2;
            var running = await sdk.RunningAvds();
            if (running.TryGetValue(avd, out var r)) await sdk.Stop(r.serial);
            return 0;
        }
        case "serial":
        {
            if (rest.Count < 1) return Fail("Usage: celestium serial <instance>");
            var avd = await Find(rest[0]); if (avd == null) return 2;
            var running = await sdk.RunningAvds();
            Console.WriteLine(running.TryGetValue(avd, out var r) ? r.serial : "emulator-" + InstanceStore.EnsurePort(avd));
            return 0;
        }
        case "install":
        {
            if (rest.Count < 2) return Fail("Usage: celestium install <instance> <file.apk>...");
            var avd = await Find(rest[0]); if (avd == null) return 2;
            var serial = await StartAndMaybeWait(avd, wait: true, cold: false);
            int failures = 0;
            foreach (var apk in rest.Skip(1))
            {
                var (code, output) = await sdk.Install(serial, Path.GetFullPath(apk));
                bool ok = code == 0 && output.Contains("Success");
                Console.WriteLine(ok ? $"Installed {Path.GetFileName(apk)} on {avd} ({serial})" : $"FAILED {apk}:\n{output.Trim()}");
                if (!ok) failures++;
            }
            return failures == 0 ? 0 : 1;
        }
        case "new":
        {
            var from = Option("--from");
            if (rest.Count < 1 || from == null) return Fail("Usage: celestium new <name> --from <instance> [--project <label>]");
            var template = await Find(from); if (template == null) return 2;
            var name = rest[0];
            AvdFactory.CreateFrom(template, name);
            if (Option("--project") is { } label) InstanceStore.SetProject(name, label);
            Console.WriteLine($"Created {name} (serial emulator-{InstanceStore.EnsurePort(name)})");
            return 0;
        }
        case "mem":
        {
            var (total, available) = MemoryGuard.SystemMemory();
            var pct = AppState.Load().MemoryBudgetPercent;
            var store = InstanceStore.Load();
            var running = await sdk.RunningAvds();
            Console.WriteLine($"Windows:      {MemoryGuard.Gb(total - available)} of {MemoryGuard.Gb(total)} GB in use ({MemoryGuard.Gb(available)} GB free)");
            Console.WriteLine($"Device limit: {(pct > 0 ? $"{pct}% = {MemoryGuard.Gb(MemoryGuard.BudgetMb(pct))} GB" : "none")}");
            foreach (var avd in running.Keys)
            {
                var info = store.TryGetValue(avd, out var i) ? i : new InstanceInfo();
                long actual = 0;
                if (Sdk.QemuPid(avd) is { } pid) try { using var p = System.Diagnostics.Process.GetProcessById(pid); actual = p.PrivateMemorySize64 / 1048576; } catch { }
                Console.WriteLine($"  {avd,-14} {MemoryGuard.Gb(MemoryGuard.DeviceRamMb(avd, info))} GB RAM setting, counts {MemoryGuard.Gb(MemoryGuard.EstimateMb(avd, info))} GB, using {MemoryGuard.Gb(actual)} GB now");
            }
            var daemons = MemoryGuard.BuildDaemons();
            Console.WriteLine($"Build daemons: {daemons.Count} using {MemoryGuard.Gb(daemons.Sum(d => d.Mb))} GB" + (daemons.Count > 0 ? "  (celestium mem --free stops idle ones)" : ""));
            if (await MemoryGuard.ComfyUiMb() is { } comfy) Console.WriteLine($"ComfyUI:      {MemoryGuard.Gb(comfy)} GB" + "  (celestium mem --free unloads its models)");
            if (Flag("--free"))
            {
                Console.WriteLine($"Stopped {await MemoryGuard.StopIdleBuildDaemons()} idle build daemon(s).");
                if (await MemoryGuard.ComfyUiMb() != null) Console.WriteLine(await MemoryGuard.UnloadComfyUiModels() ? "ComfyUI released its models." : "ComfyUI didn't respond.");
            }
            return 0;
        }
        case "memory":
        {
            if (rest.Count < 2) return Fail("Usage: celestium memory <instance> <MB|default>");
            var avd = await Find(rest[0]); if (avd == null) return 2;
            if (rest[1].Equals("default", StringComparison.OrdinalIgnoreCase)) InstanceStore.SetRam(avd, null);
            else if (int.TryParse(rest[1], out var mb) && mb >= 1024) InstanceStore.SetRam(avd, mb);
            else return Fail("Memory must be a number of MB (at least 1024) or 'default'.");
            return 0;
        }
        case "project":
        {
            if (rest.Count < 1) return Fail("Usage: celestium project <instance> [label]");
            var avd = await Find(rest[0]); if (avd == null) return 2;
            InstanceStore.SetProject(avd, rest.Count > 1 ? string.Join(' ', rest.Skip(1)) : null);
            return 0;
        }
        default:
            return Fail($"Unknown command '{args[0]}'.\n\n{Usage}");
    }
}
catch (Exception ex)
{
    return Fail(ex.Message);
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
