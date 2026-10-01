using System.Runtime.InteropServices;
using System.Windows.Interop;
using VikingClip.Core.Logging;
using VikingClip.Core.Native;
using VikingClip.Core.Settings;

namespace VikingClip.App.Services;

/// <summary>
/// Global hotkeys via RegisterHotKey on a hidden top-level window (which also receives WM_DISPLAYCHANGE).
/// RegisterHotKey is handled by the system before the game sees the key, so it works in fullscreen games
/// without hooking or injecting anything.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, string> _idToName = new();
    private readonly Dictionary<string, int> _nameToId = new();
    private int _nextId = 0xB000;

    public event Action<string>? Pressed;
    public event Action? DisplayChanged;

    public IntPtr Handle => _source.Handle;

    public HotkeyService()
    {
        var p = new HwndSourceParameters("VikingClipHotkeys")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    /// <summary>Binds a named action to a hotkey; returns null on success or a human-readable reason.</summary>
    public string? Register(string name, Hotkey hotkey)
    {
        Unregister(name);
        if (!hotkey.IsBound) return null;
        var id = _nextId++;
        var mods = (uint)hotkey.Modifiers | User32.MOD_NOREPEAT;
        if (!User32.RegisterHotKey(_source.Handle, id, mods, (uint)hotkey.VirtualKey))
        {
            var err = Marshal.GetLastWin32Error();
            var reason = err == 1409 ? $"{hotkey} is already used by another program" : $"{hotkey} could not be registered (error {err})";
            Log.Warn($"Hotkey '{name}': {reason}");
            return reason;
        }
        _idToName[id] = name;
        _nameToId[name] = id;
        Log.Info($"Hotkey '{name}' = {hotkey}");
        return null;
    }

    public void Unregister(string name)
    {
        if (_nameToId.Remove(name, out var id))
        {
            User32.UnregisterHotKey(_source.Handle, id);
            _idToName.Remove(id);
        }
    }

    public void UnregisterAll()
    {
        foreach (var name in _nameToId.Keys.ToList()) Unregister(name);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case User32.WM_HOTKEY:
                Log.Debug($"WM_HOTKEY id=0x{wParam.ToInt64():X}");
                if (_idToName.TryGetValue(wParam.ToInt32(), out var name))
                {
                    handled = true;
                    try { Pressed?.Invoke(name); }
                    catch (Exception ex) { Log.Error("Hotkey handler failed", ex); }
                }
                break;
            case User32.WM_DISPLAYCHANGE:
                try { DisplayChanged?.Invoke(); } catch { }
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
