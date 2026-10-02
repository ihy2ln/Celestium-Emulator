using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace DroidLauncher;

/// <summary>
/// Self-update from the GitHub releases of the public repo. Files in use are renamed aside (Windows allows that for
/// loaded files) and replaced, so no separate updater process is needed; the new version loads on restart.
/// Releases keep the .exe byte-identical, so normally only .dll files change and antivirus has nothing new to check.
/// </summary>
static class Updater
{
    const string Repo = "ihy2ln/Celestium-Emulator";
    const string AssetName = "CelestiumEmulator.zip";

    public sealed record Release(string Version, string Notes, string ZipUrl, string Page);

    static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CelestiumEmulator", AppInfo.Version));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>The latest release if it's newer than this build, otherwise null.</summary>
    public static async Task<Release?> CheckAsync()
    {
        using var http = NewClient();
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = tag.TrimStart('v', 'V');
        if (!IsNewer(version, AppInfo.Version)) return null;
        var zip = root.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("name").GetString() == AssetName);
        if (zip.ValueKind == JsonValueKind.Undefined) return null;
        var url = zip.GetProperty("browser_download_url").GetString() ?? "";
        // Only ever download from GitHub itself.
        if (!url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.Ordinal)) return null;
        return new Release(version, root.GetProperty("body").GetString() ?? "", url, root.GetProperty("html_url").GetString() ?? "");
    }

    public static bool IsNewer(string candidate, string current) =>
        System.Version.TryParse(candidate, out var a) && System.Version.TryParse(current, out var b) && a > b;

    /// <summary>Downloads and installs a release in place. Returns true if an .exe changed (antivirus may check it once).</summary>
    public static async Task<bool> InstallAsync(Release release, IProgress<string> progress)
    {
        var work = Path.Combine(Path.GetTempPath(), "CelestiumEmulator-update-" + release.Version);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, AssetName);

        progress.Report($"Downloading {release.Version}…");
        using (var http = NewClient())
        using (var response = await http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var src = await response.Content.ReadAsStreamAsync();
            await using var dst = File.Create(zipPath);
            var buffer = new byte[81920];
            long done = 0; int read;
            while ((read = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0) progress.Report($"Downloading {release.Version}… {done * 100 / total}%");
            }
        }

        progress.Report("Checking the download…");
        var extracted = Path.Combine(work, "files");
        ZipFile.ExtractToDirectory(zipPath, extracted);
        foreach (var required in new[] { "CelestiumEmulator.exe", "CelestiumEmulator.dll", "CelestiumEmulator.runtimeconfig.json" })
            if (!File.Exists(Path.Combine(extracted, required)))
                throw new InvalidOperationException($"The update is missing {required}; nothing was changed.");

        progress.Report("Installing…");
        var target = AppContext.BaseDirectory;
        bool exeChanged = false;
        foreach (var file in Directory.GetFiles(extracted))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(target, name);
            if (name.Equals("launcher.json", StringComparison.OrdinalIgnoreCase) && File.Exists(dest)) continue; // keep user settings
            if (File.Exists(dest) && SameFile(dest, file)) continue;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(dest)) exeChanged = true;
            try { File.Copy(file, dest, true); }
            catch (IOException)
            {
                // In use (this process or the CLI): move it aside, then put the new file in its place.
                File.Move(dest, $"{dest}.{DateTime.Now.Ticks}.old");
                File.Copy(file, dest, true);
            }
        }
        foreach (var file in Directory.GetFiles(extracted))
            if (!File.Exists(Path.Combine(target, Path.GetFileName(file))) && !file.EndsWith(".pdb"))
                throw new InvalidOperationException($"Update incomplete: {Path.GetFileName(file)} is missing.");

        try { Directory.Delete(work, true); } catch { }
        return exeChanged;
    }

    static bool SameFile(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var sha = SHA256.Create();
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return sha.ComputeHash(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }

    /// <summary>Remove files a previous update moved aside (they're no longer loaded after a restart).</summary>
    public static void CleanupOldFiles()
    {
        try
        {
            foreach (var old in Directory.GetFiles(AppContext.BaseDirectory, "*.old"))
                try { File.Delete(old); } catch { }
        }
        catch { }
    }

    /// <summary>Start the new version once this process has exited.</summary>
    public static void RestartIntoNewVersion()
    {
        using var _ = Process.Start(new ProcessStartInfo(Application.ExecutablePath, $"--after-update {Environment.ProcessId}") { UseShellExecute = false });
    }
}
