using VikingClip.Core.Media;
using Xunit;

namespace VikingClip.Core.Tests;

public class FragmentParserTests
{
    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "frag_3s.mp4"));

    [Theory]
    [InlineData(7)]
    [InlineData(100)]
    [InlineData(4096)]
    [InlineData(1 << 20)]
    public void Parses_init_and_fragments_regardless_of_chunking(int chunk)
    {
        var data = Fixture();
        var parser = new FragmentParser(sessionId: 3, sessionOffset: 1000);
        InitSegment? init = null;
        var frags = new List<Fragment>();
        parser.InitReceived += i => init = i;
        parser.FragmentReceived += f => frags.Add(f);

        for (var pos = 0; pos < data.Length; pos += chunk)
            parser.Feed(data.AsSpan(pos, Math.Min(chunk, data.Length - pos)));

        Assert.NotNull(init);
        Assert.True(init!.Timescale > 0);
        Assert.StartsWith("ftyp", System.Text.Encoding.ASCII.GetString(init.Bytes, 4, 4));
        Assert.True(frags.Count >= 3, $"expected >=3 fragments, got {frags.Count}");
        Assert.Equal(0, frags[0].RelativeStart, 3);
        // 1 s keyframe interval -> fragments of ~1 s (the last one may be shorter)
        foreach (var f in frags.Take(frags.Count - 1)) Assert.InRange(f.Duration, 0.9, 1.1);
        var total = frags.Sum(f => f.Duration);
        Assert.InRange(total, 3.0, 3.4);
        // fragments are contiguous
        for (var i = 1; i < frags.Count; i++)
            Assert.Equal(frags[i - 1].RelativeStart + frags[i - 1].Duration, frags[i].RelativeStart, 3);
        Assert.All(frags, f => Assert.Same(init, f.Init));
        Assert.All(frags, f => Assert.Equal(3, f.SessionId));
    }

    [Fact]
    public void Absolute_time_uses_session_offset_and_first_frame_pts()
    {
        var parser = new FragmentParser(1, sessionOffset: 500);
        var frags = new List<Fragment>();
        parser.FragmentReceived += frags.Add;
        parser.Feed(Fixture());
        var init = parser.Init!;

        // before ffmpeg reports the first pts, only estimates (none set here) -> base = offset
        Assert.Equal(500, frags[0].Start, 3);
        init.EstimatedFirstFramePts = 0.7;
        Assert.Equal(500.7, frags[0].Start, 3);
        init.FirstFramePts = 0.65; // exact value wins and applies retroactively
        Assert.Equal(500.65, frags[0].Start, 3);
        Assert.Equal(500.65 + frags[1].RelativeStart, frags[1].Start, 3);
        Assert.True(init.HasExactTiming);
    }

    [Fact]
    public void Garbage_resets_without_throwing()
    {
        var parser = new FragmentParser(1, 0);
        var junk = new byte[64];
        junk[3] = 2; // size 2 < header -> corrupt
        parser.Feed(junk);
        parser.Feed(Fixture());
        Assert.NotNull(parser.Init);
    }
}
