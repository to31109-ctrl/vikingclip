using System.Drawing;
using System.Drawing.Imaging;
using VikingClip.Core.Display;
using VikingClip.Core.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace VikingClip.Core.Capture;

/// <summary>
/// Grabs one frame of a monitor through DXGI Desktop Duplication (works for exclusive-fullscreen games,
/// unlike GDI). Falls back to GDI if duplication is unavailable.
/// </summary>
public static class ScreenshotGrabber
{
    public static Bitmap Grab(MonitorInfo monitor)
    {
        try
        {
            return GrabDxgi(monitor);
        }
        catch (Exception ex)
        {
            Log.Warn($"DXGI screenshot failed ({ex.Message}); using GDI");
            return GrabGdi(monitor);
        }
    }

    private static Bitmap GrabDxgi(MonitorInfo monitor)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters1((uint)monitor.AdapterIndex, out IDXGIAdapter1? adapter).CheckError();
        using (adapter)
        {
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, null,
                out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
            using (device)
            using (context)
            {
                adapter!.EnumOutputs((uint)monitor.OutputIndex, out IDXGIOutput? output).CheckError();
                using (output)
                using (var output1 = output!.QueryInterface<IDXGIOutput1>())
                using (var dup = output1.DuplicateOutput(device!))
                {
                    ID3D11Texture2D? frameTex = null;
                    var acquired = false;
                    try
                    {
                        // The first frames after DuplicateOutput can be "no change" or metadata-only; retry briefly.
                        for (var attempt = 0; attempt < 10 && !acquired; attempt++)
                        {
                            var r = dup.AcquireNextFrame(150, out var info, out IDXGIResource? resource);
                            if (r.Failure)
                            {
                                if (r.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) continue;
                                r.CheckError();
                            }
                            using (resource)
                            {
                                if (info.LastPresentTime == 0 && attempt < 5)
                                {
                                    dup.ReleaseFrame();
                                    continue;
                                }
                                frameTex = resource!.QueryInterface<ID3D11Texture2D>();
                                acquired = true;
                            }
                        }
                        if (!acquired || frameTex is null) throw new InvalidOperationException("No desktop frame available");

                        var desc = frameTex.Description;
                        var stagingDesc = new Texture2DDescription
                        {
                            Width = desc.Width,
                            Height = desc.Height,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = desc.Format,
                            SampleDescription = new SampleDescription(1, 0),
                            Usage = ResourceUsage.Staging,
                            BindFlags = BindFlags.None,
                            CPUAccessFlags = CpuAccessFlags.Read,
                            MiscFlags = ResourceOptionFlags.None,
                        };
                        using var staging = device!.CreateTexture2D(stagingDesc);
                        context!.CopyResource(staging, frameTex);
                        var map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                        try
                        {
                            var w = (int)desc.Width;
                            var h = (int)desc.Height;
                            var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                            try
                            {
                                unsafe
                                {
                                    var src = (byte*)map.DataPointer;
                                    var dst = (byte*)data.Scan0;
                                    var rowBytes = w * 4;
                                    for (var y = 0; y < h; y++)
                                        Buffer.MemoryCopy(src + y * (int)map.RowPitch, dst + y * data.Stride, rowBytes, rowBytes);
                                }
                            }
                            finally { bmp.UnlockBits(data); }
                            return bmp;
                        }
                        finally { context.Unmap(staging, 0); }
                    }
                    finally
                    {
                        frameTex?.Dispose();
                        if (acquired) { try { dup.ReleaseFrame(); } catch { } }
                    }
                }
            }
        }
    }

    private static Bitmap GrabGdi(MonitorInfo monitor)
    {
        var b = monitor.Bounds;
        var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(b.Left, b.Top, 0, 0, new Size(b.Width, b.Height), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static void SavePng(Bitmap bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        bmp.Save(path, ImageFormat.Png);
    }
}
