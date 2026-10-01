namespace VikingClip.Core.Capture;

/// <summary>
/// One way of turning captured desktop frames into H.264. The GPU-frame plans never copy pixels to the CPU
/// (lowest overhead). The "sw" plans download frames once (needed when the encoder lives on another GPU
/// or when the vendor's RGB conversion can't be trusted) and the last resort is libx264 on the CPU.
/// </summary>
public sealed record EncoderPlan(
    string Id,
    string DisplayName,
    string Vendor,          // NVIDIA / AMD / Intel / Any
    string Encoder,         // ffmpeg encoder name
    bool GpuFrames,         // frames stay in D3D11 textures all the way to the encoder
    bool Hardware)
{
    /// <summary>Colour matrix the encoder really applies when converting RGB; the file is tagged to match.
    /// NVENC converts RGB input with BT.601 (measured). The sw paths convert explicitly to BT.709.</summary>
    public string ColorMatrix => GpuFrames && Encoder == "h264_nvenc" ? "smpte170m" : "bt709";

    /// <summary>Output size after the optional resolution cap (even dimensions, aspect kept).</summary>
    public static (int Width, int Height) OutputSize(int srcWidth, int srcHeight, int maxHeight)
    {
        if (maxHeight <= 0 || srcHeight <= maxHeight) return (srcWidth & ~1, srcHeight & ~1);
        var w = (int)Math.Round(srcWidth * (double)maxHeight / srcHeight / 2) * 2;
        return (w, maxHeight & ~1);
    }

    /// <summary>Filters inserted after ddagrab/setpts and before the encoder.</summary>
    public string FilterSuffix(int maxHeight, int srcWidth, int srcHeight, bool gpuScalerAvailable)
    {
        var (w, h) = OutputSize(srcWidth, srcHeight, maxHeight);
        var scaleNeeded = h != srcHeight;
        if (GpuFrames)
        {
            return scaleNeeded && gpuScalerAvailable ? $",scale_d3d11=width={w}:height={h}" : "";
        }
        var scale = scaleNeeded
            ? $",scale=w={w}:h={h}:out_color_matrix=bt709:out_range=tv"
            : ",scale=out_color_matrix=bt709:out_range=tv";
        return ",hwdownload,format=bgra" + scale + ",format=nv12";
    }

    /// <summary>
    /// Quality-first settings: VBR around the target with headroom for motion, a constant-quality floor where the
    /// encoder supports it, and no B-frames (clips are cut at any frame after a keyframe).
    /// </summary>
    public IEnumerable<string> EncoderArgs(int kbps, int fps)
    {
        var maxrate = $"{(int)(kbps * 1.5)}k";
        var bufsize = $"{kbps * 3}k";
        var common = new[] { "-g", $"{fps}", "-bf", "0", "-profile:v", "high" };
        var specific = Encoder switch
        {
            "h264_nvenc" => new[]
            {
                "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "19", "-b:v", $"{kbps}k", "-maxrate", maxrate, "-bufsize", bufsize,
                "-multipass", "qres", "-spatial-aq", "1", "-aq-strength", "8", "-rc-lookahead", "16", "-forced-idr", "1",
            },
            "h264_amf" => new[]
            {
                "-usage", "transcoding", "-quality", "quality", "-rc", "vbr_peak", "-b:v", $"{kbps}k", "-maxrate", maxrate, "-bufsize", bufsize,
                "-forced_idr", "1",
            },
            "h264_qsv" => new[] { "-preset", "medium", "-b:v", $"{kbps}k", "-maxrate", maxrate, "-bufsize", bufsize, "-forced_idr", "1" },
            "libx264" => new[] { "-preset", "veryfast", "-tune", "zerolatency", "-crf", "20", "-maxrate", maxrate, "-bufsize", bufsize },
            _ => new[] { "-b:v", $"{kbps}k" },
        };
        return common.Concat(specific);
    }

    public static readonly EncoderPlan Nvenc = new("nvenc", "NVIDIA NVENC (GPU)", "NVIDIA", "h264_nvenc", true, true);
    public static readonly EncoderPlan Amf = new("amf", "AMD AMF (GPU)", "AMD", "h264_amf", true, true);
    public static readonly EncoderPlan NvencSw = new("nvenc-sw", "NVIDIA NVENC (CPU copy)", "NVIDIA", "h264_nvenc", false, true);
    public static readonly EncoderPlan AmfSw = new("amf-sw", "AMD AMF (CPU copy)", "AMD", "h264_amf", false, true);
    public static readonly EncoderPlan QsvSw = new("qsv", "Intel Quick Sync", "Intel", "h264_qsv", false, true);
    public static readonly EncoderPlan X264 = new("x264", "Software x264 (CPU, slow)", "Any", "libx264", false, false);

    public static readonly EncoderPlan[] All = { Nvenc, Amf, NvencSw, AmfSw, QsvSw, X264 };

    public static EncoderPlan? ById(string? id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>The CPU-copy sibling of a GPU-frame plan (used when the output must be downscaled, since
    /// the D3D11 scaler filter is not usable with desktop-duplication frames in the bundled ffmpeg).</summary>
    public EncoderPlan ScalableVariant() => this switch
    {
        _ when ReferenceEquals(this, Nvenc) => NvencSw,
        _ when ReferenceEquals(this, Amf) => AmfSw,
        _ => this,
    };

    /// <summary>Probe order for a monitor on an adapter of <paramref name="vendor"/>, given which GPU vendors exist in the PC.</summary>
    public static IEnumerable<EncoderPlan> CandidatesFor(string vendor, IReadOnlyCollection<string> presentVendors)
    {
        var list = new List<EncoderPlan>();
        switch (vendor)
        {
            case "NVIDIA": list.Add(Nvenc); list.Add(NvencSw); break;
            case "AMD": list.Add(Amf); list.Add(AmfSw); break;
            case "Intel": list.Add(QsvSw); break;
        }
        // Encoders on other GPUs in the system can still take CPU-side frames.
        if (vendor != "NVIDIA" && presentVendors.Contains("NVIDIA")) list.Add(NvencSw);
        if (vendor != "AMD" && presentVendors.Contains("AMD")) list.Add(AmfSw);
        if (vendor != "Intel" && presentVendors.Contains("Intel")) list.Add(QsvSw);
        list.Add(X264);
        return list.Distinct();
    }
}
