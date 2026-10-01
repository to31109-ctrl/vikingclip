using VikingClip.Core.Media;
using Xunit;

namespace VikingClip.Core.Tests;

public class FragmentRingTests
{
    private static InitSegment Init(int session, double offset = 0) =>
        new() { Bytes = new byte[16], Timescale = 1000, SessionId = session, SessionOffset = offset, FirstFramePts = 0 };

    private static Fragment Frag(InitSegment init, double start, double dur = 1, int bytes = 10) =>
        new() { Bytes = new byte[bytes], Init = init, RelativeStart = start, Duration = dur };

    [Fact]
    public void Evicts_by_duration()
    {
        var ring = new FragmentRing(5);
        var init = Init(1);
        for (var i = 0; i < 20; i++) ring.Add(Frag(init, i));
        Assert.InRange(ring.BufferedSeconds, 5, 6.01);
        Assert.Equal(20, ring.LatestEnd);
        Assert.True(ring.OldestStart >= 14);
    }

    [Fact]
    public void Evicts_by_bytes()
    {
        var ring = new FragmentRing(1000) { MaxBytes = 55 };
        var init = Init(1);
        for (var i = 0; i < 20; i++) ring.Add(Frag(init, i));
        Assert.True(ring.Bytes <= 55);
        Assert.True(ring.Count >= 5);
    }

    [Fact]
    public void Range_returns_overlapping_fragments()
    {
        var ring = new FragmentRing(100);
        var init = Init(1);
        for (var i = 0; i < 10; i++) ring.Add(Frag(init, i));
        var r = ring.GetRange(2.5, 5.2, out var truncated);
        Assert.False(truncated);
        Assert.Equal(new[] { 2.0, 3.0, 4.0, 5.0 }, r.Select(f => f.Start));
    }

    [Fact]
    public void Range_across_sessions_keeps_newest_session_and_flags_it()
    {
        var ring = new FragmentRing(100);
        var a = Init(1);
        var b = Init(2, offset: 5); // restarted ffmpeg: its own pts 0 = engine 5 s
        for (var i = 0; i < 5; i++) ring.Add(Frag(a, i));
        for (var i = 0; i < 5; i++) ring.Add(Frag(b, i));
        var r = ring.GetRange(3, 8, out var truncated);
        Assert.True(truncated);
        Assert.All(r, f => Assert.Equal(2, f.SessionId));
        Assert.Equal(new[] { 5.0, 6.0, 7.0 }, r.Select(f => f.Start));
    }

    [Fact]
    public async Task WaitForCoverage_returns_when_fragment_arrives()
    {
        var ring = new FragmentRing(100);
        var init = Init(1);
        var task = ring.WaitForCoverageAsync(1.5, TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        ring.Add(Frag(init, 0));
        ring.Add(Frag(init, 1));
        Assert.True(await task);
        Assert.False(await ring.WaitForCoverageAsync(10, TimeSpan.FromMilliseconds(150)));
    }
}
