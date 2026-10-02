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
        // The first copy is the "primary": it owns the tray icon, reopens last session's instances and
        // remembers the window position. More copies can be opened as ordinary extra windows.
        using var mutex = new Mutex(true, PrimaryMutexName, out bool primary);
        bool forceNewWindow = args.Contains("--new-window");

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
