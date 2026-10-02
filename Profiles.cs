namespace DroidLauncher;

/// <summary>Screen hardware a new instance is created with. Generic form factors, not brand names.</summary>
record DeviceProfile(string Name, string Description, int Width, int Height, int Dpi, bool Foldable = false)
{
    public static readonly DeviceProfile[] All =
    {
        new("Phone", "6.2\" · 1080 × 2340 · 420 dpi", 1080, 2340, 420),
        new("Phone Plus", "6.7\" · 1080 × 2400 · 400 dpi", 1080, 2400, 400),
        new("Phone Ultra", "6.8\" QHD+ · 1440 × 3120 · 500 dpi", 1440, 3120, 500),
        new("Compact", "5.8\" · 1080 × 2280 · 440 dpi", 1080, 2280, 440),
        new("Foldable", "7.6\" inner screen · 1768 × 2208 · 420 dpi", 1768, 2208, 420, Foldable: true),
        new("Tablet", "11\" · 1600 × 2560 · 320 dpi", 1600, 2560, 320),
    };

    public static DeviceProfile? Find(string? name) => All.FirstOrDefault(p => p.Name == name);
}

/// <summary>One-click performance levels. They set RAM and CPU cores; GPU is always host-accelerated.</summary>
record PerfPreset(string Name, string Description, int RamMb, int Cores)
{
    public static readonly PerfPreset[] All =
    {
        new("Low", "2 GB · 2 cores · lightest, for background test devices", 2048, 2),
        new("Balanced", "3 GB · 4 cores · everyday apps", 3072, 4),
        new("High", "4 GB · 6 cores · heavier apps and most games", 4096, 6),
        new("Ultra", "6 GB · 8 cores · demanding 3D games", 6144, 8),
    };

    public static PerfPreset? Find(string? name) => All.FirstOrDefault(p => p.Name == name);

    /// <summary>Never ask for more cores than the PC has.</summary>
    public int EffectiveCores => Math.Min(Cores, Math.Max(1, Environment.ProcessorCount - 1));
}

static class CameraModes
{
    public static readonly (string Value, string Label)[] All =
    {
        ("emulated", "Virtual scene"),
        ("webcam0", "PC webcam"),
        ("none", "Off"),
    };

    public static string Label(string? value) => All.FirstOrDefault(m => m.Value == value).Label ?? "Virtual scene";
}

/// <summary>GPS presets for the controls panel.</summary>
static class Places
{
    public static readonly (string Name, double Lat, double Lon)[] All =
    {
        ("New York", 40.7128, -74.0060),
        ("Los Angeles", 34.0522, -118.2437),
        ("Chicago", 41.8781, -87.6298),
        ("Albuquerque", 35.0844, -106.6504),
        ("London", 51.5074, -0.1278),
        ("Paris", 48.8566, 2.3522),
        ("Berlin", 52.5200, 13.4050),
        ("Tokyo", 35.6762, 139.6503),
        ("Seoul", 37.5665, 126.9780),
        ("Sydney", -33.8688, 151.2093),
        ("São Paulo", -23.5505, -46.6333),
        ("Mumbai", 19.0760, 72.8777),
    };
}
