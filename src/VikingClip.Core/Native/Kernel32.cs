using System.Runtime.InteropServices;
using System.Text;

namespace VikingClip.Core.Native;

public static class Kernel32
{
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetPackageFullName(IntPtr hProcess, ref uint packageFullNameLength, StringBuilder? packageFullName);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength, StringBuilder? path);

    public static string? GetProcessPath(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>For packaged (Microsoft Store / Xbox app) games: returns the package install folder, else null.</summary>
    public static (string FullName, string Path)? GetPackageInfo(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            uint len = 0;
            var rc = GetPackageFullName(h, ref len, null);
            if (rc != 122 /* ERROR_INSUFFICIENT_BUFFER */ || len == 0) return null;
            var name = new StringBuilder((int)len);
            if (GetPackageFullName(h, ref len, name) != 0) return null;

            uint plen = 0;
            rc = GetPackagePathByFullName(name.ToString(), ref plen, null);
            if (rc != 122 || plen == 0) return (name.ToString(), "");
            var path = new StringBuilder((int)plen);
            if (GetPackagePathByFullName(name.ToString(), ref plen, path) != 0) return (name.ToString(), "");
            return (name.ToString(), path.ToString());
        }
        finally { CloseHandle(h); }
    }
}

public static class Shell32
{
    public enum UserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3dFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7,
    }

    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out UserNotificationState state);

    public static UserNotificationState QueryUserNotificationState() =>
        SHQueryUserNotificationState(out var s) == 0 ? s : UserNotificationState.AcceptsNotifications;
}

public static class Dwmapi
{
    public const int DWMWA_CLOAKED = 14;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    public static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
}
