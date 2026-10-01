using System.Text;
using System.Text.RegularExpressions;

namespace VikingClip.Core.Util;

public static partial class FileNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Turns an arbitrary game title into a safe folder/file name component.</summary>
    public static string Sanitize(string? name, string fallback = "Game")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (Array.IndexOf(invalid, ch) >= 0 || char.IsControl(ch)) sb.Append(' ');
            else sb.Append(ch);
        }
        var cleaned = MultiSpace().Replace(sb.ToString(), " ").Trim(' ', '.');
        if (cleaned.Length > 60) cleaned = cleaned[..60].TrimEnd(' ', '.');
        if (cleaned.Length == 0 || Reserved.Contains(cleaned)) return fallback;
        return cleaned;
    }

    public static string Timestamp(DateTime t) => t.ToString("yyyy-MM-dd HH-mm-ss");

    /// <summary>
    /// Builds e.g. &lt;root&gt;\VALORANT\VALORANT 2026-09-30 21-15-03.mp4, adding " (2)" etc. if it exists.
    /// </summary>
    public static string BuildOutputPath(string root, string gameFolder, string gameLabel, string extension, DateTime when)
    {
        var dir = Path.Combine(root, Sanitize(gameFolder));
        Directory.CreateDirectory(dir);
        var stem = $"{Sanitize(gameLabel)} {Timestamp(when)}";
        var path = Path.Combine(dir, stem + extension);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{stem} ({i}){extension}");
        return path;
    }

    public static string HumanSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{v:0} {units[u]}" : $"{v:0.#} {units[u]}";
    }

    public static string HumanDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();
}
