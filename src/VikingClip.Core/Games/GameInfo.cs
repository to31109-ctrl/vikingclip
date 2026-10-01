using VikingClip.Core.Util;

namespace VikingClip.Core.Games;

/// <summary>What was in the foreground when a hotkey fired, resolved to a game (or not).</summary>
public sealed record GameInfo(string Name, bool IsGame, string Source, string? ExePath)
{
    /// <summary>Folder under the clips root.</summary>
    public string FolderName => IsGame ? FileNames.Sanitize(Name) : Paths.DesktopFolderName;

    /// <summary>Prefix used in file names.</summary>
    public string Label => IsGame ? FileNames.Sanitize(Name) : "Desktop";

    public string ExeName => ExePath is null ? "" : Path.GetFileName(ExePath);

    public static GameInfo Desktop(string? exePath) => new("Desktop", false, "desktop", exePath);
}
