namespace VikingClip.Core;

/// <summary>Well-known folders and file locations. Settings/logs live in roaming AppData so they survive
/// app updates and uninstall/reinstall (the installer owns %LocalAppData%\VikingClip).</summary>
public static class Paths
{
    public static string AppDataDir => EnsureDir(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VikingClip"));

    public static string LogsDir => EnsureDir(Path.Combine(AppDataDir, "logs"));
    public static string ThumbnailsDir => EnsureDir(Path.Combine(AppDataDir, "thumbnails"));
    public static string TempDir => EnsureDir(Path.Combine(Path.GetTempPath(), "VikingClip"));
    public static string SettingsFile => Path.Combine(AppDataDir, "settings.json");
    public static string LibraryIndexFile => Path.Combine(AppDataDir, "library.json");

    public static string DefaultClipsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "VikingClip");

    /// <summary>Folder used when the foreground app is not a game (or nothing is in the foreground).</summary>
    public const string DesktopFolderName = "Desktop";

    /// <summary>Locates ffmpeg.exe: env override, then the copy bundled next to the app, then PATH.</summary>
    public static string? FindFfmpeg()
    {
        var env = Environment.GetEnvironmentVariable("VIKINGCLIP_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"),
                     Path.Combine(baseDir, "ffmpeg.exe"),
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    public static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    public static string NewTempDir(string prefix)
    {
        var dir = Path.Combine(TempDir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
