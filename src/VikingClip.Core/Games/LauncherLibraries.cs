using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using VikingClip.Core.Logging;

namespace VikingClip.Core.Games;

public sealed record InstalledGame(string Directory, string Name, string Source);

/// <summary>
/// Reads the install databases of Steam, Epic and Riot so any game from those launchers is recognised
/// by its install folder, even if it isn't in known-games.json.
/// </summary>
public sealed partial class LauncherLibraries
{
    private List<InstalledGame> _entries = new();
    private DateTime _loadedAt = DateTime.MinValue;
    private readonly object _gate = new();

    public IReadOnlyList<InstalledGame> Entries
    {
        get { Refresh(force: false); lock (_gate) return _entries; }
    }

    public InstalledGame? Lookup(string exePath)
    {
        Refresh(force: false);
        List<InstalledGame> entries;
        lock (_gate) entries = _entries;
        InstalledGame? best = null;
        foreach (var e in entries)
        {
            if (exePath.StartsWith(e.Directory, StringComparison.OrdinalIgnoreCase) &&
                (exePath.Length == e.Directory.Length || exePath[e.Directory.Length] == '\\' || e.Directory.EndsWith('\\')))
            {
                if (best is null || e.Directory.Length > best.Directory.Length) best = e;
            }
        }
        return best;
    }

    public void Refresh(bool force)
    {
        lock (_gate)
        {
            if (!force && DateTime.UtcNow - _loadedAt < TimeSpan.FromMinutes(5)) return;
            var list = new List<InstalledGame>();
            try { list.AddRange(ScanSteam()); } catch (Exception ex) { Log.Warn("Steam scan failed: " + ex.Message); }
            try { list.AddRange(ScanEpic()); } catch (Exception ex) { Log.Warn("Epic scan failed: " + ex.Message); }
            try { list.AddRange(ScanRiot()); } catch (Exception ex) { Log.Warn("Riot scan failed: " + ex.Message); }
            _entries = list.Select(e => e with { Directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(e.Directory)) }).ToList();
            _loadedAt = DateTime.UtcNow;
            Log.Debug($"Launcher libraries: {_entries.Count} installed games");
        }
    }

    // ---- Steam ------------------------------------------------------------------------------

    private static IEnumerable<InstalledGame> ScanSteam()
    {
        var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                        ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath)) yield break;
        steamPath = steamPath.Replace('/', '\\');

        var libraries = new List<string> { steamPath };
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf)) libraries.AddRange(ParseLibraryFolders(File.ReadAllText(vdf)));

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var steamapps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(steamapps)) continue;
            foreach (var acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                InstalledGame? g = null;
                try
                {
                    var parsed = ParseAppManifest(File.ReadAllText(acf));
                    if (parsed is { } p && !IsSteamTool(p.Name))
                        g = new InstalledGame(Path.Combine(steamapps, "common", p.InstallDir), p.Name, "steam");
                }
                catch { }
                if (g is not null) yield return g;
            }
        }
    }

    public static IEnumerable<string> ParseLibraryFolders(string vdf)
    {
        foreach (Match m in VdfPath().Matches(vdf))
            yield return m.Groups[1].Value.Replace(@"\\", @"\").Replace('/', '\\');
    }

    public static (string Name, string InstallDir)? ParseAppManifest(string acf)
    {
        var name = VdfKey("name").Match(acf);
        var dir = VdfKey("installdir").Match(acf);
        if (!name.Success || !dir.Success) return null;
        return (Unescape(name.Groups[1].Value), Unescape(dir.Groups[1].Value));
    }

    private static bool IsSteamTool(string name) =>
        name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Dedicated Server", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Steamworks Common Redistributables", StringComparison.OrdinalIgnoreCase);

    private static string Unescape(string s) => s.Replace(@"\\", @"\").Replace("\\\"", "\"");

    private static Regex VdfKey(string key) => new($"\"{key}\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase);

    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPath();

    // ---- Epic -------------------------------------------------------------------------------

    private static IEnumerable<InstalledGame> ScanEpic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            InstalledGame? g = null;
            try { g = ParseEpicItem(File.ReadAllText(item)); } catch { }
            if (g is not null) yield return g;
        }
    }

    public static InstalledGame? ParseEpicItem(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var name = root.TryGetProperty("DisplayName", out var dn) ? dn.GetString() : null;
        var loc = root.TryGetProperty("InstallLocation", out var il) ? il.GetString() : null;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(loc)) return null;
        if (root.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cats.EnumerateArray())
            {
                var v = c.GetString();
                if (v is "plugins" or "plugins/engine" or "engines") return null;
            }
        }
        if (name.StartsWith("UE_", StringComparison.OrdinalIgnoreCase)) return null;
        return new InstalledGame(loc.Replace('/', '\\'), name, "epic");
    }

    // ---- Riot -------------------------------------------------------------------------------

    private static readonly Dictionary<string, string> RiotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["valorant"] = "VALORANT",
        ["league_of_legends"] = "League of Legends",
        ["bacon"] = "Legends of Runeterra",
        ["2xko"] = "2XKO",
        ["lor"] = "Legends of Runeterra",
    };

    private static IEnumerable<InstalledGame> ScanRiot()
    {
        var meta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games", "Metadata");
        if (!Directory.Exists(meta)) yield break;
        foreach (var yaml in Directory.EnumerateFiles(meta, "*.product_settings.yaml", SearchOption.AllDirectories))
        {
            InstalledGame? g = null;
            try
            {
                var product = Path.GetFileName(yaml).Split('.')[0];
                var m = RiotInstallPath().Match(File.ReadAllText(yaml));
                if (m.Success)
                {
                    var path = m.Groups[1].Value.Trim('"', '\'').Replace('/', '\\');
                    var name = RiotNames.TryGetValue(product, out var n) ? n : product.Replace('_', ' ');
                    g = new InstalledGame(path, name, "riot");
                }
            }
            catch { }
            if (g is not null) yield return g;
        }
    }

    [GeneratedRegex(@"product_install_full_path:\s*(.+)")]
    private static partial Regex RiotInstallPath();
}
