using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VikingClip.Core.Native;

namespace VikingClip.App.Services;

/// <summary>Win32 helpers for our windows: dark title bar, capture exclusion, no-activate, physical-pixel placement.</summary>
public static class WindowNative
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).EnsureHandle();

    /// <summary>Native dark title bar matching the theme (Windows 10 20H1+ / 11).</summary>
    public static void ApplyDarkTitleBar(Window w)
    {
        var h = Handle(w);
        var on = 1;
        DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        var caption = 0x001E1815; // COLORREF 0x00BBGGRR of #15181E
        var text = 0x00F0EBE8;    // #E8EBF0
        var border = 0x003B312B;  // #2B313B
        DwmSetWindowAttribute(h, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
        DwmSetWindowAttribute(h, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    /// <summary>The window never shows up in clips/recordings/screenshots taken by any capture API.</summary>
    public static void ExcludeFromCapture(Window w) =>
        User32.SetWindowDisplayAffinity(Handle(w), User32.WDA_EXCLUDEFROMCAPTURE);

    /// <summary>Tool window (no taskbar/alt-tab entry); optionally never takes focus (toasts, indicator).</summary>
    public static void MakeOverlay(Window w, bool noActivate)
    {
        var h = Handle(w);
        var ex = User32.GetWindowLong(h, User32.GWL_EXSTYLE);
        ex |= User32.WS_EX_TOOLWINDOW | User32.WS_EX_TOPMOST;
        if (noActivate) ex |= User32.WS_EX_NOACTIVATE;
        User32.SetWindowLong(h, User32.GWL_EXSTYLE, ex);
    }

    /// <summary>Moves the window (physical pixels) without changing size or z-order.</summary>
    public static void MovePhysical(Window w, int x, int y, bool activate = false)
    {
        var flags = User32.SWP_NOSIZE | User32.SWP_NOZORDER | (activate ? 0 : User32.SWP_NOACTIVATE);
        User32.SetWindowPos(Handle(w), IntPtr.Zero, x, y, 0, 0, flags);
    }

    public static RECT PhysicalRect(Window w)
    {
        User32.GetWindowRect(Handle(w), out var r);
        return r;
    }

    public static void CenterOnMonitor(Window w, RECT monitor)
    {
        var r = PhysicalRect(w);
        var x = monitor.Left + (monitor.Width - r.Width) / 2;
        var y = monitor.Top + (monitor.Height - r.Height) / 2;
        MovePhysical(w, x, y);
    }

    public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

    public static void PlaceAtCorner(Window w, RECT monitor, Corner corner, int margin)
    {
        var r = PhysicalRect(w);
        var x = corner is Corner.TopLeft or Corner.BottomLeft ? monitor.Left + margin : monitor.Right - margin - r.Width;
        var y = corner is Corner.TopLeft or Corner.TopRight ? monitor.Top + margin : monitor.Bottom - margin - r.Height;
        MovePhysical(w, x, y);
    }

    public static void Topmost(Window w) =>
        User32.SetWindowPos(Handle(w), User32.HWND_TOPMOST, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
}
