using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DroidLauncher;

/// <summary>
/// Keeps devices from eating the PC: all running devices together stay within a budget (a share of physical RAM),
/// and a device only starts if Windows actually has room for it plus a safety margin.
/// </summary>
static class MemoryGuard
{
    /// <summary>Emulator overhead on top of the guest's RAM: QEMU itself, GPU emulation buffers, the window.</summary>
    public const int OverheadMb = 1536;
    /// <summary>Always leave this much free for Windows and other apps.</summary>
    public const int HeadroomMb = 2048;
    public const int MinDeviceRamMb = 2048;

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    public static (long totalMb, long availableMb) SystemMemory()
    {
        var s = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref s);
        return ((long)(s.TotalPhys / 1048576), (long)(s.AvailPhys / 1048576));
    }

    public static int DeviceRamMb(string avd, InstanceInfo info) =>
        info.RamMb ?? (AvdFactory.ConfiguredRamMb(avd) is > 0 and var mb ? mb : 2048);

    public static int EstimateMb(string avd, InstanceInfo info) => DeviceRamMb(avd, info) + OverheadMb;

    public static long BudgetMb(int percent) => percent <= 0 ? long.MaxValue : SystemMemory().totalMb * percent / 100;

    /// <summary>
    /// Null if the device may start with <paramref name="ramMb"/> of RAM; otherwise why not, plus the most RAM that
    /// would fit (0 if even the minimum doesn't).
    /// </summary>
    public static string? Check(string avd, int ramMb, IEnumerable<(string avd, InstanceInfo info)> running, int budgetPercent, out int fitsMb)
    {
        var others = running.Where(r => !r.avd.Equals(avd, StringComparison.OrdinalIgnoreCase)).ToList();
        long usedByDevices = others.Sum(r => (long)EstimateMb(r.avd, r.info));
        long budget = BudgetMb(budgetPercent);
        var (total, available) = SystemMemory();

        // The most this device could have: within the budget and within what's actually free.
        long room = Math.Min(budget - usedByDevices, available - HeadroomMb) - OverheadMb;
        fitsMb = room >= MinDeviceRamMb ? (int)(Math.Min(room, ramMb) / 512 * 512) : 0;

        long need = ramMb + OverheadMb;
        if (budgetPercent > 0 && usedByDevices + need > budget)
            return $"Running it would put your devices at {Gb(usedByDevices + need)} GB, over the {budgetPercent}% device limit ({Gb(budget)} GB of {Gb(total)} GB)." +
                   (others.Count > 0 ? $"\nAlready running: {string.Join(", ", others.Select(o => $"{o.avd} ({Gb(EstimateMb(o.avd, o.info))} GB)"))}." : "");
        if (need + HeadroomMb > available)
            return $"Windows only has {Gb(available)} GB free right now; {avd} needs about {Gb(need)} GB plus {Gb(HeadroomMb)} GB to spare.";
        return null;
    }

    public static string Gb(long mb) => (mb / 1024.0).ToString("0.#");

    // ── Other memory users the app can help with ───────────────────────────

    public sealed record JavaDaemon(int Pid, string Kind, long Mb);

    /// <summary>Gradle and Kotlin build daemons left running after Android builds (they idle for up to 3 hours).</summary>
    public static List<JavaDaemon> BuildDaemons()
    {
        var list = new List<JavaDaemon>();
        foreach (var (pid, cmd) in JavaCommandLines())
        {
            var kind = cmd.Contains("GradleDaemon") ? "Gradle daemon" : cmd.Contains("KotlinCompileDaemon") ? "Kotlin compile daemon" : null;
            if (kind == null) continue;
            try { using var p = Process.GetProcessById(pid); list.Add(new JavaDaemon(pid, kind, p.WorkingSet64 / 1048576)); } catch { }
        }
        return list;
    }

    /// <summary>Command lines of java.exe processes, via WMI over COM (no extra packages).</summary>
    static List<(int pid, string cmd)> JavaCommandLines()
    {
        var result = new List<(int, string)>();
        try
        {
            dynamic locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!)!;
            dynamic service = locator.ConnectServer(".", "root\\cimv2");
            dynamic rows = service.ExecQuery("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='java.exe'");
            foreach (dynamic row in rows)
                result.Add(((int)row.ProcessId, (string?)row.CommandLine ?? ""));
        }
        catch { }
        return result;
    }

    /// <summary>Ends build daemons that aren't doing anything (CPU idle over a short sample). Returns how many.</summary>
    public static async Task<int> StopIdleBuildDaemons()
    {
        var daemons = BuildDaemons();
        var before = daemons.ToDictionary(d => d.Pid, d => CpuMs(d.Pid));
        await Task.Delay(1500);
        int stopped = 0;
        foreach (var d in daemons)
        {
            var cpu = CpuMs(d.Pid) - before[d.Pid];
            if (cpu > 150) continue; // busy: a build is running
            try { using var p = Process.GetProcessById(d.Pid); p.Kill(); stopped++; } catch { }
        }
        return stopped;
    }

    static double CpuMs(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.TotalProcessorTime.TotalMilliseconds; } catch { return 0; }
    }

    /// <summary>ComfyUI on its default port, if running: (working set MB). AI models stay in RAM/VRAM after a job.</summary>
    public static async Task<long?> ComfyUiMb()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var stats = await http.GetStringAsync("http://127.0.0.1:8188/system_stats");
            if (!stats.Contains("comfyui")) return null;
            long mb = 0;
            foreach (var p in Process.GetProcessesByName("python"))
            {
                try { if (p.MainModule?.FileName?.Contains("ComfyUI", StringComparison.OrdinalIgnoreCase) == true) mb += p.WorkingSet64 / 1048576; } catch { }
                finally { p.Dispose(); }
            }
            return mb;
        }
        catch { return null; }
    }

    public static async Task<bool> UnloadComfyUiModels()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var r = await http.PostAsync("http://127.0.0.1:8188/free",
                new StringContent("{\"unload_models\": true, \"free_memory\": true}", System.Text.Encoding.UTF8, "application/json"));
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
