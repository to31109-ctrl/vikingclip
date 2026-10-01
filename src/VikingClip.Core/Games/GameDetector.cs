using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VikingClip.Core.Native;
using VikingClip.Core.Settings;

namespace VikingClip.Core.Games;

/// <summary>Decides which game (if any) a foreground window belongs to, NVIDIA-style folder sorting.</summary>
public sealed partial class GameDetector
{
    private readonly KnownGames _known;
    private readonly LauncherLibraries _libraries;
    private readonly Func<IReadOnlyDictionary<string, GameOverride>> _overrides;

    public GameDetector(Func<IReadOnlyDictionary<string, GameOverride>> overrides, KnownGames? known = null, LauncherLibraries? libraries = null)
    {
        _overrides = overrides;
        _known = known ?? KnownGames.Load();
        _libraries = libraries ?? new LauncherLibraries();
    }

    public LauncherLibraries Libraries => _libraries;
    public KnownGames Known => _known;

    public GameInfo Detect(ForegroundInfo fg)
    {
        if (fg.Hwnd == IntPtr.Zero || string.IsNullOrEmpty(fg.ExePath) || fg.ProcessId == Environment.ProcessId)
            return GameInfo.Desktop(fg.ExePath);

        var exe = fg.ExeName;
        return Classify(exe, fg.ExePath, fg.Title, fg.ProcessId, fg.IsExclusiveFullscreen || fg.CoversMonitor);
    }

    /// <summary>Pure classification (testable without a real window).</summary>
    public GameInfo Classify(string exe, string exePath, string title, uint pid, bool fullscreenLike)
    {
        if (_overrides().TryGetValue(exe, out var o))
        {
            if (o.NotAGame) return GameInfo.Desktop(exePath);
            if (!string.IsNullOrWhiteSpace(o.Name)) return new GameInfo(o.Name, true, "override", exePath);
        }

        if (_known.Games.TryGetValue(exe, out var knownName))
            return new GameInfo(knownName, true, "known", exePath);

        foreach (var rule in _known.TitleRules)
        {
            if (string.Equals(rule.Exe, exe, StringComparison.OrdinalIgnoreCase) &&
                (rule.TitleContains.Length == 0 || title.Contains(rule.TitleContains, StringComparison.OrdinalIgnoreCase)))
                return new GameInfo(rule.Name, true, "title", exePath);
        }

        if (_libraries.Lookup(exePath) is { } installed)
            return new GameInfo(installed.Name, true, installed.Source, exePath);

        if (_known.NotGames.Contains(exe))
            return GameInfo.Desktop(exePath);

        if (pid != 0 && Kernel32.GetPackageInfo(pid) is { } pkg)
        {
            var name = PackageDisplayName(pkg.FullName, pkg.Path);
            return fullscreenLike ? new GameInfo(name, true, "store", exePath) : GameInfo.Desktop(exePath);
        }

        if (fullscreenLike)
            return new GameInfo(GuessName(exePath, title), true, "heuristic", exePath);

        return GameInfo.Desktop(exePath);
    }

    private static readonly HashSet<string> GenericProductNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "Unreal Engine", "UnrealEngine", "Unity", "Unity Player", "Microsoft® Windows® Operating System",
        "Microsoft Windows Operating System", "Godot Engine", "Game", "Launcher", "Shipping",
    };

    public static string GuessName(string exePath, string title)
    {
        string? product = null, description = null;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            product = vi.ProductName?.Trim();
            description = vi.FileDescription?.Trim();
        }
        catch { }

        if (product is not null && !GenericProductNames.Contains(product) && product.Length <= 60) return CleanName(product);
        if (description is not null && !GenericProductNames.Contains(description) && description.Length <= 60 &&
            !description.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return CleanName(description);
        return CleanName(CleanExeStem(Path.GetFileNameWithoutExtension(exePath)));
    }

    /// <summary>"FortniteClient-Win64-Shipping" → "Fortnite Client"</summary>
    public static string CleanExeStem(string stem)
    {
        var s = StemNoise().Replace(stem, " ");
        s = s.Replace('_', ' ').Replace('-', ' ');
        s = CamelCase().Replace(s, "$1 $2");
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return s.Length == 0 ? stem : s;
    }

    private static string CleanName(string name)
    {
        var s = name.Replace("™", "").Replace("®", "").Trim();
        return s.Length == 0 ? name : s;
    }

    private static string PackageDisplayName(string fullName, string path)
    {
        try
        {
            var manifest = Path.Combine(path, "AppxManifest.xml");
            if (File.Exists(manifest))
            {
                var doc = XDocument.Load(manifest);
                var ns = doc.Root?.GetDefaultNamespace();
                var dn = doc.Root?.Element(ns! + "Properties")?.Element(ns + "DisplayName")?.Value;
                if (!string.IsNullOrWhiteSpace(dn) && !dn.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase)) return dn.Trim();
            }
        }
        catch { }
        // Microsoft.MinecraftUWP_1.21.0.0_x64__8wekyb3d8bbwe → MinecraftUWP → Minecraft
        var name = fullName.Split('_')[0];
        var dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        name = name.Replace("UWP", "");
        return CleanExeStem(name);
    }

    // Trailing boundary is "not a letter/digit" so '_' and '-' count as separators ("my_cool_game_x64" -> "my cool").
    [GeneratedRegex(@"(?i)[-_ ]?(win64|win32|x64|x86|shipping|dx11|dx12|vulkan|steam|epic|client|launcher|game)(?![a-z0-9])")]
    private static partial Regex StemNoise();

    [GeneratedRegex(@"([a-z])([A-Z])")]
    private static partial Regex CamelCase();
}
