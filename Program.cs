namespace DroidLauncher;

static class Program
{
    const string PrimaryMutexName = "CelestiumEmulator.Primary";
    const string ShowEventName = "CelestiumEmulator.ShowWindow";
    const string PrimaryVisibleEventName = "CelestiumEmulator.PrimaryVisible";

    /// <summary>Set while the primary window is on screen; cleared while it's hidden in the tray.</summary>
    public static EventWaitHandle? PrimaryVisible;

    [STAThread]
    static void Main(string[] args)
    {
        // Restarting after a self-update: let the old copy exit first so this one becomes the primary.
        var afterUpdate = Array.IndexOf(args, "--after-update");
        if (afterUpdate >= 0 && afterUpdate + 1 < args.Length && int.TryParse(args[afterUpdate + 1], out var oldPid))
        {
            try { using var old = System.Diagnostics.Process.GetProcessById(oldPid); old.WaitForExit(15_000); } catch { }
        }

        // The first copy is the "primary": it owns the tray icon, reopens last session's instances and
        // remembers the window position. More copies can be opened as ordinary extra windows.
        using var mutex = new Mutex(true, PrimaryMutexName, out bool primary);
        bool forceNewWindow = args.Contains("--new-window");
        // Optional: open straight to a device and tab, e.g. --select Dev --tab 2
        string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        MainForm.StartSelect = Arg("--select");
        MainForm.ForceEdges = args.Contains("--force-edges"); // testing: edge resizing in an extra window
        MainForm.CheckUpdateOnStart = args.Contains("--check-update");
        if (int.TryParse(Arg("--tab"), out var tab)) MainForm.StartTab = Math.Clamp(tab - 1, 0, 5);

        if (!primary && !forceNewWindow)
        {
            // If the primary is hidden in the tray, launching again just brings it back.
            try
            {
                using var visible = EventWaitHandle.OpenExisting(PrimaryVisibleEventName);
                if (!visible.WaitOne(0))
                {
                    using var show = EventWaitHandle.OpenExisting(ShowEventName);
                    show.Set();
                    return;
                }
            }
            catch (WaitHandleCannotBeOpenedException) { }
        }

        ApplicationConfiguration.Initialize();
        var form = new MainForm(Config.Load(), AppState.Load(), primary);

        if (primary)
        {
            PrimaryVisible = new EventWaitHandle(false, EventResetMode.ManualReset, PrimaryVisibleEventName);
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            new Thread(() =>
            {
                while (showEvent.WaitOne())
                    try { form.BeginInvoke(new Action(form.ShowFromTray)); } catch { return; }
            }) { IsBackground = true }.Start();
        }
        Application.Run(form);
        GC.KeepAlive(mutex);
    }
}
