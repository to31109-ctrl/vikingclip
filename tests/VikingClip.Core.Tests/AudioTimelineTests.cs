using VikingClip.Core.Audio;
using Xunit;

namespace VikingClip.Core.Tests;

public class AudioTimelineTests
{
    private static AudioChunk Chunk(double start, int frames, short value = 1000)
    {
        var s = new short[frames * 2];
        Array.Fill(s, value);
        return new AudioChunk { Start = start, Samples = s };
    }

    private static short[] Read(MemoryStream ms)
    {
        var bytes = ms.ToArray();
        var shorts = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, shorts, 0, bytes.Length);
        return shorts;
    }

    [Fact]
    public void Exact_length_with_gap_filled_by_silence()
    {
        var chunks = new[] { Chunk(10.0, 4800), Chunk(10.2, 4800) }; // 0.1 s hole between 10.1 and 10.2
        using var ms = new MemoryStream();
        var frames = PcmTimelineWriter.WriteRange(ms, chunks, 10.0, 10.5);
        Assert.Equal(24000, frames);
        var pcm = Read(ms);
        Assert.Equal(48000, pcm.Length);
        Assert.Equal(1000, pcm[0]);
        Assert.Equal(0, pcm[4800 * 2 + 100]);        // in the hole
        Assert.Equal(1000, pcm[(int)(0.25 * 48000) * 2]); // second chunk
        Assert.Equal(0, pcm[^1]);                     // padded tail
    }

    [Fact]
    public void Leading_partial_chunk_is_trimmed_and_overlap_dropped()
    {
        var chunks = new[] { Chunk(9.95, 4800, 1), Chunk(10.05, 4800, 2), Chunk(10.14, 4800, 3) }; // 2nd and 3rd overlap by 10 ms
        using var ms = new MemoryStream();
        var frames = PcmTimelineWriter.WriteRange(ms, chunks, 10.0, 10.3);
        Assert.Equal(14400, frames);
        var pcm = Read(ms);
        Assert.Equal(1, pcm[0]);                       // chunk 1 from its 0.05 s mark
        Assert.Equal(2, pcm[(int)(0.1 * 48000) * 2]);
        Assert.Equal(3, pcm[(int)(0.2 * 48000) * 2]);
        Assert.Equal(14400 * 2, pcm.Length);
    }

    [Fact]
    public void Live_writer_pads_to_finish_time()
    {
        using var ms = new MemoryStream();
        var w = new PcmTimelineWriter(ms, 100);
        w.Append(Chunk(99.9, 4800));  // straddles the start
        w.Append(Chunk(100.0, 4800)); // overlaps completely with what we already wrote
        w.FinishAt(101);
        Assert.Equal(48000, w.FramesWritten);
        Assert.Equal(48000 * 4, ms.Length);
    }

    [Fact]
    public void Ring_evicts_old_chunks()
    {
        var ring = new AudioRing(2);
        for (var i = 0; i < 10; i++) ring.Add(Chunk(i, 48000));
        var all = ring.GetRange(0, 100);
        Assert.True(all[0].Start >= 7);
        Assert.Equal(10, all[^1].End);
    }
}
