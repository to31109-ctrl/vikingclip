namespace VikingClip.Core.Native;

/// <summary>Snapshot of what the user is looking at the instant a hotkey fires.</summary>
public sealed record ForegroundInfo(
    IntPtr Hwnd,
    uint ProcessId,
    string? ExePath,
    string Title,
    RECT WindowRect,
    IntPtr HMonitor,
    RECT MonitorRect,
    bool IsExclusiveFullscreen,
    bool CoversMonitor)
{
    public string ExeName => ExePath is null ? "" : Path.GetFileName(ExePath);

    public static ForegroundInfo Capture()
    {
        var hwnd = User32.GetForegroundWindow();
        uint pid = 0;
        if (hwnd != IntPtr.Zero) User32.GetWindowThreadProcessId(hwnd, out pid);

        var exe = pid != 0 ? Kernel32.GetProcessPath(pid) : null;
        var title = hwnd != IntPtr.Zero ? User32.GetWindowTitle(hwnd) : "";

        User32.GetWindowRect(hwnd, out var wr);

        // Monitor: the one with the foreground window, else the one under the cursor, else primary.
        var hMon = hwnd != IntPtr.Zero ? User32.MonitorFromWindow(hwnd, User32.MONITOR_DEFAULTTONULL) : IntPtr.Zero;
        if (hMon == IntPtr.Zero && User32.GetCursorPos(out var pt)) hMon = User32.MonitorFromPoint(pt, User32.MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) hMon = User32.MonitorFromPoint(new POINT(), User32.MONITOR_DEFAULTTOPRIMARY);

        var mi = User32.GetMonitorInfoEx(hMon);
        var mr = mi?.rcMonitor ?? default;

        var state = Shell32.QueryUserNotificationState();
        var exclusive = state == Shell32.UserNotificationState.RunningD3dFullScreen;

        var covers = mr.Width > 0 && wr.Width >= mr.Width - 2 && wr.Height >= mr.Height - 2
                     && wr.Left <= mr.Left + 1 && wr.Top <= mr.Top + 1;

        return new ForegroundInfo(hwnd, pid, exe, title, wr, hMon, mr, exclusive, covers);
    }
}
