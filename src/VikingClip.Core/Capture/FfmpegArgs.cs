using System.Globalization;
using VikingClip.Core.Display;

namespace VikingClip.Core.Capture;

/// <summary>Parameters that shape the capture pipeline. Changing any of them restarts the capture.</summary>
public sealed record CaptureParams(int Fps, int BitrateKbps, int MaxHeight, bool DrawCursor);

/// <summary>Builds ffmpeg command lines. Kept in one place so the pipeline is easy to read and tweak.</summary>
public static class FfmpegArgs
{
    public const string KeyframeExpr = "expr:isnan(prev_forced_t)+gte(t,prev_forced_t+1)";

    /// <summary>
    /// Target bitrate for the resolution/fps actually captured. 40 Mbps at 1080p60 is in ShadowPlay's "high"
    /// territory; the encoders run VBR with a quality floor, so static content uses less and motion can peak above.
    /// </summary>
    public static int AutoBitrateKbps(int width, int height, int fps)
    {
        var pixels = (double)width * height;
        var basePer1080p60 = 40000.0;
        var kbps = basePer1080p60 * (pixels / (1920.0 * 1080.0)) * Math.Pow(fps / 60.0, 0.6);
        return (int)Math.Clamp(Math.Round(kbps / 500) * 500, 8000, 120000);
    }

    /// <summary>The long-running per-monitor capture: desktop → GPU encoder → fragmented MP4 on stdout.
    /// A one-frame side branch prints the first frame's wall-clock pts so fragments can be placed on the engine timeline.</summary>
    public static List<string> Capture(MonitorInfo m, EncoderPlan plan, CaptureParams p, long sessionEpochMicros, bool gpuScalerAvailable)
    {
        var inv = CultureInfo.InvariantCulture;
        var filter =
            $"ddagrab=output_idx={m.OutputIndex}:framerate={p.Fps}:draw_mouse={(p.DrawCursor ? 1 : 0)}" +
            $",setpts=(RTCTIME-{sessionEpochMicros.ToString(inv)})/(TB*1000000)" +
            $",setparams=range=tv:colorspace={plan.ColorMatrix}:color_primaries=bt709:color_trc=bt709" +
            plan.FilterSuffix(p.MaxHeight, m.Width, m.Height, gpuScalerAvailable) +
            // 'print' only prints frames that carry metadata, so add a dummy entry first.
            ",split[v][t];[t]trim=end_frame=1,metadata=mode=add:key=vikingclip:value=1,metadata=mode=print[ts]";

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "info", "-nostats",
            "-init_hw_device", $"d3d11va=d3d:{m.AdapterIndex}", "-filter_hw_device", "d3d",
            "-filter_complex", filter,
            "-map", "[v]",
            "-c:v", plan.Encoder,
        };
        args.AddRange(plan.EncoderArgs(p.BitrateKbps, p.Fps));
        args.AddRange(new[]
        {
            "-force_key_frames", KeyframeExpr,
            "-colorspace", plan.ColorMatrix, "-color_range", "tv", "-color_primaries", "bt709", "-color_trc", "bt709",
            "-fps_mode", "vfr",
            "-f", "mp4", "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-flush_packets", "1",
            "pipe:1",
            "-map", "[ts]", "-fps_mode", "passthrough", "-f", "null", "-",
        });
        return args;
    }

    /// <summary>Short self-test of a plan on a monitor: 15 frames to the null muxer.</summary>
    public static List<string> Probe(MonitorInfo m, EncoderPlan plan, bool gpuScalerAvailable)
    {
        var filter = $"ddagrab=output_idx={m.OutputIndex}:framerate=30:draw_mouse=0" +
                     ",setpts=(RTCTIME-0)/(TB*1000000)" +
                     $",setparams=range=tv:colorspace={plan.ColorMatrix}:color_primaries=bt709:color_trc=bt709" +
                     plan.FilterSuffix(0, m.Width, m.Height, gpuScalerAvailable);
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostats",
            "-init_hw_device", $"d3d11va=d3d:{m.AdapterIndex}", "-filter_hw_device", "d3d",
            "-filter_complex", filter,
            "-frames:v", "15",
            "-c:v", plan.Encoder,
        };
        args.AddRange(plan.EncoderArgs(8000, 30));
        args.AddRange(new[] { "-f", "null", "-" });
        return args;
    }

    /// <summary>Combines a video file (fragments) with raw PCM tracks into the final MP4. Video is copied, never re-encoded.</summary>
    public static List<string> Mux(
        string videoPath, string? desktopPcm, string? micPcm,
        double audioSkipSeconds, double durationSeconds,
        float desktopGain, float micGain, string outputPath)
    {
        var inv = CultureInfo.InvariantCulture;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-i", videoPath };
        var idx = 1;
        int? d = null, mic = null;
        if (desktopPcm is not null)
        {
            if (audioSkipSeconds > 0) args.AddRange(new[] { "-ss", audioSkipSeconds.ToString("0.###", inv) });
            args.AddRange(new[] { "-f", "s16le", "-ar", "48000", "-ac", "2", "-i", desktopPcm });
            d = idx++;
        }
        if (micPcm is not null)
        {
            if (audioSkipSeconds > 0) args.AddRange(new[] { "-ss", audioSkipSeconds.ToString("0.###", inv) });
            args.AddRange(new[] { "-f", "s16le", "-ar", "48000", "-ac", "2", "-i", micPcm });
            mic = idx++;
        }

        args.AddRange(new[] { "-map", "0:v:0", "-c:v", "copy" });
        if (d is not null && mic is not null)
        {
            var fc = $"[{d}:a]volume={desktopGain.ToString("0.##", inv)}[dv];[{mic}:a]volume={micGain.ToString("0.##", inv)}[mv];" +
                     "[dv][mv]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.95:level=false[mix]";
            args.AddRange(new[]
            {
                "-filter_complex", fc,
                "-map", "[mix]", "-map", $"{d}:a", "-map", $"{mic}:a",
                "-c:a", "aac", "-b:a", "192k",
                "-metadata:s:a:0", "title=Mixed", "-metadata:s:a:1", "title=Desktop", "-metadata:s:a:2", "title=Microphone",
                "-disposition:a:0", "default", "-disposition:a:1", "0", "-disposition:a:2", "0",
            });
        }
        else if (d is not null || mic is not null)
        {
            var only = d ?? mic!.Value;
            var gain = d is not null ? desktopGain : micGain;
            args.AddRange(new[]
            {
                "-map", $"{only}:a", "-c:a", "aac", "-b:a", "192k",
                "-filter:a", $"volume={gain.ToString("0.##", inv)}",
                "-metadata:s:a:0", d is not null ? "title=Desktop" : "title=Microphone",
            });
        }
        args.AddRange(new[] { "-t", durationSeconds.ToString("0.###", inv), "-movflags", "+faststart", outputPath });
        return args;
    }

    /// <summary>Concatenates same-codec MP4 parts (recording that spanned an ffmpeg restart).</summary>
    public static List<string> Concat(string listFile, string outputPath) =>
        new() { "-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", listFile, "-c", "copy", "-movflags", "+faststart", outputPath };

    public static List<string> Thumbnail(string videoPath, string outputJpg, double atSeconds) =>
        new()
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-ss", atSeconds.ToString("0.##", CultureInfo.InvariantCulture), "-i", videoPath,
            "-frames:v", "1", "-vf", "scale=400:-2", "-q:v", "4", outputJpg,
        };

    /// <summary>Two-pass x264 re-encode to a size target (Discord). Pass 1 writes stats, pass 2 the file.</summary>
    public static List<string> Compress(string input, string output, int width, int height, int fps, int videoKbps, int audioKbps, int pass, string passLog, int threads)
    {
        var inv = CultureInfo.InvariantCulture;
        var vf = $"scale={width}:{height}:flags=lanczos,fps={fps}";
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y", "-i", input,
            "-map", "0:v:0",
            "-vf", vf,
            "-c:v", "libx264", "-preset", "medium", "-profile:v", "high", "-pix_fmt", "yuv420p",
            "-b:v", $"{videoKbps}k", "-maxrate", $"{(int)(videoKbps * 1.15)}k", "-bufsize", $"{videoKbps * 2}k",
            "-pass", pass.ToString(inv), "-passlogfile", passLog,
            "-threads", threads.ToString(inv),
        };
        if (pass == 1)
        {
            args.AddRange(new[] { "-an", "-f", "null", "NUL" });
        }
        else
        {
            args.AddRange(new[] { "-map", "0:a:0?", "-c:a", "aac", "-b:a", $"{audioKbps}k", "-ac", "2", "-movflags", "+faststart", output });
        }
        return args;
    }
}
