namespace VikingClip.Core.Util;

/// <summary>
/// The single timeline everything in the engine is stamped with: seconds since the engine's epoch.
/// ffmpeg writes the same wall-clock (via setpts/RTCTIME) into the video fragments, audio chunks are
/// stamped on arrival, so clips can be cut by time across video and audio without guesswork.
/// </summary>
public sealed class CaptureClock
{
    /// <summary>Engine epoch as Unix time in microseconds (same unit ffmpeg's RTCTIME uses).</summary>
    public long EpochUnixMicros { get; }

    public CaptureClock() : this(UnixMicrosNow()) { }
    public CaptureClock(long epochUnixMicros) => EpochUnixMicros = epochUnixMicros;

    /// <summary>Seconds since epoch, now.</summary>
    public double Now => (UnixMicrosNow() - EpochUnixMicros) / 1_000_000.0;

    public double FromUnixMicros(long unixMicros) => (unixMicros - EpochUnixMicros) / 1_000_000.0;

    public DateTime ToLocalTime(double engineSeconds) =>
        DateTime.UnixEpoch.AddTicks(EpochUnixMicros * 10 + (long)(engineSeconds * 10_000_000)).ToLocalTime();

    public static long UnixMicrosNow() => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;
}
