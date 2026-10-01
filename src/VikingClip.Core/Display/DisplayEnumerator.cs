using Microsoft.Win32;
using VikingClip.Core.Logging;
using VikingClip.Core.Native;
using Vortice.DXGI;

namespace VikingClip.Core.Display;

/// <summary>One physical monitor, with the DXGI adapter/output indices ffmpeg's ddagrab needs.</summary>
public sealed record MonitorInfo(
    int AdapterIndex,
    int OutputIndex,
    string AdapterName,
    uint VendorId,
    string DeviceName,
    IntPtr HMonitor,
    RECT Bounds,
    bool IsPrimary)
{
    public int Width => Bounds.Width;
    public int Height => Bounds.Height;

    public string Vendor => VendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        _ => "Other",
    };

    /// <summary>Stable id for settings (e.g. \\.\DISPLAY1).</summary>
    public string Key => DeviceName;

    public string Label
    {
        get
        {
            var n = DeviceName.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase) ? DeviceName[11..] : DeviceName;
            return $"Monitor {n} · {Width}×{Height}{(IsPrimary ? " · primary" : "")}";
        }
    }
}

public static class DisplayEnumerator
{
    public static List<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success && adapter is not null; a++)
            {
                using (adapter)
                {
                    var ad = adapter.Description1;
                    if ((ad.Flags & AdapterFlags.Software) != 0) continue;

                    for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success && output is not null; o++)
                    {
                        using (output)
                        {
                            var od = output.Description;
                            if (!od.AttachedToDesktop) continue;
                            var rc = od.DesktopCoordinates;
                            var bounds = new RECT { Left = rc.Left, Top = rc.Top, Right = rc.Right, Bottom = rc.Bottom };
                            var mi = User32.GetMonitorInfoEx(od.Monitor);
                            var primary = mi is { } m && (m.dwFlags & User32.MONITORINFOF_PRIMARY) != 0;
                            list.Add(new MonitorInfo((int)a, (int)o, ad.Description.Trim(), ad.VendorId, od.DeviceName, od.Monitor, bounds, primary));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("DXGI enumeration failed", ex);
        }

        // Primary first, then left-to-right.
        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();
    }

    public static MonitorInfo? FindByHMonitor(IReadOnlyList<MonitorInfo> monitors, IntPtr hMonitor)
    {
        if (hMonitor != IntPtr.Zero)
        {
            var hit = monitors.FirstOrDefault(m => m.HMonitor == hMonitor);
            if (hit is not null) return hit;
            // HMONITOR handles can be recycled after a display change; fall back to geometry.
            var mi = User32.GetMonitorInfoEx(hMonitor);
            if (mi is { } info)
                return monitors.FirstOrDefault(m => string.Equals(m.DeviceName, info.szDevice, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    /// <summary>Adapter/driver fingerprint: when it changes, the encoder probe runs again.</summary>
    public static string Fingerprint(IReadOnlyList<MonitorInfo> monitors, string? ffmpegPath)
    {
        var adapters = string.Join(";", monitors.Select(m => $"{m.AdapterIndex}:{m.AdapterName}:{m.VendorId:X}").Distinct());
        var drivers = ReadDisplayDriverVersions();
        string ff = "";
        try
        {
            if (ffmpegPath is not null && File.Exists(ffmpegPath))
            {
                var fi = new FileInfo(ffmpegPath);
                ff = $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
            }
        }
        catch { }
        return $"{adapters}|{drivers}|{ff}";
    }

    private static string ReadDisplayDriverVersions()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (key is null) return "";
            var parts = new List<string>();
            foreach (var sub in key.GetSubKeyNames())
            {
                if (!int.TryParse(sub, out _)) continue;
                using var k = key.OpenSubKey(sub);
                var v = k?.GetValue("DriverVersion") as string;
                var desc = k?.GetValue("DriverDesc") as string;
                if (v is not null) parts.Add($"{desc}={v}");
            }
            return string.Join(",", parts);
        }
        catch { return ""; }
    }
}
