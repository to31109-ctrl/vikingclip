using VikingClip.Core.Audio;
using VikingClip.Core.Logging;
using VikingClip.Core.Media;
using VikingClip.Core.Settings;

namespace VikingClip.Core.Capture;

public sealed record ClipResult(bool Success, string? Path, double DurationSeconds, long Bytes, string? Error, bool Truncated = false)
{
    public static ClipResult Fail(string error) => new(false, null, 0, 0, error);
}

/// <summary>Cuts a clip out of the replay buffers and muxes it with audio. Video is never re-encoded.</summary>
public static class ClipWriter
{
    public static async Task<ClipResult> WriteAsync(
        MonitorCapture capture, AudioRing? desktop, AudioRing? mic, AudioSettings audio,
        double endTime, double lengthSeconds, string outputPath, CancellationToken ct = default)
    {
        var ring = capture.Ring;

        // The fragment containing endTime completes at the next keyframe (≤ 1 s later). Wait for it so the
        // clip really ends at the hotkey press, not up to a second before.
        await ring.WaitForCoverageAsync(endTime, TimeSpan.FromSeconds(2.5), ct).ConfigureAwait(false);

        var from = endTime - lengthSeconds;
        var frags = ring.GetRange(from, endTime, out var truncated);
        if (frags.Count == 0)
            return ClipResult.Fail(capture.State == CaptureState.Running
                ? "Nothing in the replay buffer yet - try again in a second."
                : $"Capture is not running on this monitor ({capture.LastError ?? capture.State.ToString()}).");

        var videoStart = frags[0].Start;
        var duration = endTime - videoStart;
        if (duration < 0.3) return ClipResult.Fail("Clip would be shorter than a frame.");

        var tmp = Paths.NewTempDir("clip");
        try
        {
            var videoPath = Path.Combine(tmp, "video.mp4");
            await using (var fs = new FileStream(videoPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                await fs.WriteAsync(frags[0].Init.Bytes, ct).ConfigureAwait(false);
                foreach (var f in frags) await fs.WriteAsync(f.Bytes, ct).ConfigureAwait(false);
            }

            var offset = audio.OffsetMs / 1000.0;
            string? desktopPcm = null, micPcm = null;
            if (desktop is not null)
            {
                desktopPcm = Path.Combine(tmp, "desktop.pcm");
                WritePcm(desktopPcm, desktop, videoStart - offset, endTime - offset);
            }
            if (mic is not null)
            {
                micPcm = Path.Combine(tmp, "mic.pcm");
                WritePcm(micPcm, mic, videoStart - offset, endTime - offset);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var args = FfmpegArgs.Mux(videoPath, desktopPcm, micPcm, 0, duration, audio.DesktopGain, audio.MicrophoneGain, outputPath);
            var r = await Ffmpeg.RunAsync(args, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            if (!r.Success)
            {
                Log.Error($"Clip mux failed: {r.StdErr}");
                return ClipResult.Fail("Could not write the clip: " + r.Summary);
            }

            var size = new FileInfo(outputPath).Length;
            Log.Info($"Clip saved {outputPath} ({duration:0.0}s, {size / 1024 / 1024} MB, exactTiming={frags[0].Init.HasExactTiming})");
            return new ClipResult(true, outputPath, duration, size, null, truncated);
        }
        catch (Exception ex)
        {
            Log.Error("Clip failed", ex);
            return ClipResult.Fail(ex.Message);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    private static void WritePcm(string path, AudioRing ring, double from, double to)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        PcmTimelineWriter.WriteRange(fs, ring.GetRange(from, to), from, to);
    }
}
