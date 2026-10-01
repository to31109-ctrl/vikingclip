using System.Diagnostics;
using VikingClip.Core.Capture;
using VikingClip.Core.Logging;
using VikingClip.Core.Media;

namespace VikingClip.Core.Discord;

/// <summary>Shrinks a clip to fit a Discord upload limit with a two-pass x264 encode at low CPU priority.</summary>
public static class DiscordCompressor
{
    public sealed record Plan(int Width, int Height, int Fps, int VideoKbps, int AudioKbps, long LimitBytes)
    {
        public long EstimatedBytes(double durationSeconds) => (long)((VideoKbps + AudioKbps) * 1000.0 / 8 * durationSeconds * 1.04);
    }

    public static bool NeedsCompression(long fileBytes, long limitBytes) => fileBytes > limitBytes * 0.98;

    /// <summary>
    /// Chooses resolution/bitrates so the output lands under the limit with margin. Frame rate is kept
    /// (60 fps stays 60 fps); resolution gives way first.
    /// </summary>
    public static Plan MakePlan(double durationSeconds, long limitBytes, int srcWidth, int srcHeight, int srcFps)
    {
        durationSeconds = Math.Max(1, durationSeconds);
        var totalKbps = limitBytes * 8.0 / 1000.0 * 0.92 / durationSeconds;
        var audioKbps = totalKbps >= 2500 ? 128 : totalKbps >= 800 ? 96 : 64;
        var videoKbps = (int)Math.Max(150, totalKbps - audioKbps);

        var height = videoKbps switch
        {
            >= 7000 => 1080,
            >= 3500 => 720,
            >= 1500 => 540,
            _ => 480,
        };
        height = Math.Min(height, srcHeight);
        var fps = Math.Min(60, Math.Max(1, srcFps));
        var width = (int)Math.Round(srcWidth * (double)height / srcHeight / 2) * 2;
        height &= ~1;
        return new Plan(width, height, fps, videoKbps, audioKbps, limitBytes);
    }

    public static async Task<(bool Ok, string? Error)> CompressAsync(string input, string output, Plan plan, double durationSeconds, IProgress<string>? progress, CancellationToken ct = default)
    {
        var threads = Math.Max(1, Environment.ProcessorCount / 2);
        var passLog = Path.Combine(Paths.TempDir, $"x264-{Guid.NewGuid():N}");
        var kbps = plan.VideoKbps;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                progress?.Report(attempt == 0 ? "Compressing for Discord (pass 1 of 2)…" : "Still too big, compressing more…");
                var p1 = await Ffmpeg.RunAsync(FfmpegArgs.Compress(input, output, plan.Width, plan.Height, plan.Fps, kbps, plan.AudioKbps, 1, passLog, threads),
                    TimeSpan.FromMinutes(20), ct, ProcessPriorityClass.BelowNormal).ConfigureAwait(false);
                if (!p1.Success) return (false, "Compression failed: " + p1.Summary);

                progress?.Report("Compressing for Discord (pass 2 of 2)…");
                var p2 = await Ffmpeg.RunAsync(FfmpegArgs.Compress(input, output, plan.Width, plan.Height, plan.Fps, kbps, plan.AudioKbps, 2, passLog, threads),
                    TimeSpan.FromMinutes(20), ct, ProcessPriorityClass.BelowNormal).ConfigureAwait(false);
                if (!p2.Success) return (false, "Compression failed: " + p2.Summary);

                var size = new FileInfo(output).Length;
                Log.Info($"Compressed {Path.GetFileName(input)} -> {size / 1024.0 / 1024.0:0.0} MB at {kbps} kbps ({plan.Width}x{plan.Height}@{plan.Fps})");
                if (size <= plan.LimitBytes) return (true, null);

                var ratio = plan.LimitBytes / (double)size;
                kbps = Math.Max(100, (int)(kbps * ratio * 0.9));
            }
            return (false, "Could not get the clip under the Discord size limit.");
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(Paths.TempDir, Path.GetFileName(passLog) + "*"))
            {
                try { File.Delete(f); } catch { }
            }
        }
    }
}
