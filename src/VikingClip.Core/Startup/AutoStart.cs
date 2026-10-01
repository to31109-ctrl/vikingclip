using Microsoft.Win32;
using VikingClip.Core.Logging;

namespace VikingClip.Core.Startup;

/// <summary>Per-user "start with Windows" via HKCU\...\Run (no admin rights needed).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "VikingClip";
    public const string MinimizedArg = "--minimized";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    public static void Set(bool enabled, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(ValueName, $"\"{exePath}\" {MinimizedArg}");
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Info($"Auto-start {(enabled ? "enabled" : "disabled")} ({exePath})");
        }
        catch (Exception ex)
        {
            Log.Error("Could not update auto-start", ex);
        }
    }
}
