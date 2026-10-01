using System.Runtime.InteropServices;

namespace VikingClip.Core.Audio;

/// <summary>A block of interleaved 16-bit stereo 48 kHz PCM with its engine-time position.</summary>
public sealed class AudioChunk
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    public required double Start { get; init; }
    public required short[] Samples { get; init; }
    public int Frames => Samples.Length / Channels;
    public double Duration => Frames / (double)SampleRate;
    public double End => Start + Duration;
}

/// <summary>Replay buffer for one audio source.</summary>
public sealed class AudioRing
{
    private readonly List<AudioChunk> _chunks = new();
    private readonly object _gate = new();

    public double MaxSeconds { get; set; }
    public event Action<AudioChunk>? ChunkAdded;

    public AudioRing(double maxSeconds) => MaxSeconds = maxSeconds;

    public void Add(AudioChunk c)
    {
        lock (_gate)
        {
            _chunks.Add(c);
            while (_chunks.Count > 1 && c.End - _chunks[0].End > MaxSeconds) _chunks.RemoveAt(0);
        }
        ChunkAdded?.Invoke(c);
    }

    public void Clear() { lock (_gate) _chunks.Clear(); }

    public List<AudioChunk> GetRange(double from, double to)
    {
        lock (_gate) return _chunks.Where(c => c.End > from && c.Start < to).ToList();
    }

    public long Bytes { get { lock (_gate) return _chunks.Sum(c => (long)c.Samples.Length * 2); } }
}

/// <summary>
/// Lays chunks onto an exact sample grid starting at a given time: fills gaps with silence, drops overlaps,
/// so the output is always precisely (to - from) seconds of s16le stereo 48 kHz. Used for clips (from a ring)
/// and, incrementally, for recordings (live chunks).
/// </summary>
public sealed class PcmTimelineWriter
{
    private readonly Stream _out;
    private readonly double _start;
    private long _cursorFrames;
    private readonly byte[] _silence = new byte[48000 * 4]; // 1 s

    public PcmTimelineWriter(Stream output, double startTime)
    {
        _out = output;
        _start = startTime;
    }

    public long FramesWritten => _cursorFrames;
    public double WrittenSeconds => _cursorFrames / (double)AudioChunk.SampleRate;

    public void Append(AudioChunk chunk)
    {
        var chunkStartFrame = (long)Math.Round((chunk.Start - _start) * AudioChunk.SampleRate);
        var chunkEndFrame = chunkStartFrame + chunk.Frames;
        if (chunkEndFrame <= _cursorFrames) return; // entirely in the past

        if (chunkStartFrame > _cursorFrames) WriteSilence(chunkStartFrame - _cursorFrames);

        var skip = (int)Math.Max(0, _cursorFrames - chunkStartFrame);
        var framesToWrite = chunk.Frames - skip;
        if (framesToWrite <= 0) return;
        var bytes = MemoryMarshal.AsBytes(chunk.Samples.AsSpan(skip * AudioChunk.Channels, framesToWrite * AudioChunk.Channels));
        _out.Write(bytes);
        _cursorFrames += framesToWrite;
    }

    /// <summary>Pads with silence (or truncates nothing) so the stream ends exactly at <paramref name="endTime"/>.</summary>
    public void FinishAt(double endTime)
    {
        var target = (long)Math.Round((endTime - _start) * AudioChunk.SampleRate);
        if (target > _cursorFrames) WriteSilence(target - _cursorFrames);
        _out.Flush();
    }

    private void WriteSilence(long frames)
    {
        while (frames > 0)
        {
            var n = (int)Math.Min(frames, 48000);
            _out.Write(_silence, 0, n * 4);
            _cursorFrames += n;
            frames -= n;
        }
    }

    /// <summary>Convenience: write exactly [from, to) from a set of chunks.</summary>
    public static long WriteRange(Stream output, IEnumerable<AudioChunk> chunks, double from, double to)
    {
        var w = new PcmTimelineWriter(output, from);
        foreach (var c in chunks.OrderBy(c => c.Start))
        {
            if (c.Start >= to) break;
            if (c.End > to)
            {
                // Trim the tail so we never write past 'to'.
                var keep = (int)Math.Round((to - c.Start) * AudioChunk.SampleRate);
                if (keep <= 0) break;
                var trimmed = new AudioChunk { Start = c.Start, Samples = c.Samples.AsSpan(0, Math.Min(c.Samples.Length, keep * AudioChunk.Channels)).ToArray() };
                w.Append(trimmed);
                break;
            }
            w.Append(c);
        }
        w.FinishAt(to);
        return w.FramesWritten;
    }
}
