namespace VikingClip.Core.Media;

/// <summary>
/// The replay buffer for one monitor: the last N seconds of already-encoded video, held in RAM.
/// Saving a clip is just concatenating the init segment and a range of fragments - no re-encoding.
/// </summary>
public sealed class FragmentRing
{
    private readonly List<Fragment> _frags = new();
    private readonly object _gate = new();
    private long _bytes;

    private double _maxSeconds;

    /// <summary>Seconds of video to keep (clip length plus a safety margin).</summary>
    public double MaxSeconds
    {
        get => _maxSeconds;
        set
        {
            _maxSeconds = value;
            // Hard byte cap in case timing ever goes wrong: generous for the configured length, never unbounded.
            MaxBytes = Math.Max(200L * 1024 * 1024, EstimateBytes(value, 200_000));
        }
    }

    /// <summary>Hard cap, derived from MaxSeconds (≈100 Mbps worth) so a timing bug can't eat all RAM. Settable for tests.</summary>
    public long MaxBytes { get; set; }

    public event Action<Fragment>? FragmentAdded;

    public FragmentRing(double maxSeconds) => MaxSeconds = maxSeconds;

    public long Bytes { get { lock (_gate) return _bytes; } }
    public int Count { get { lock (_gate) return _frags.Count; } }

    public double? OldestStart { get { lock (_gate) return _frags.Count == 0 ? null : _frags[0].Start; } }
    public double? LatestEnd { get { lock (_gate) return _frags.Count == 0 ? null : _frags[^1].End; } }
    public double BufferedSeconds { get { lock (_gate) return _frags.Count == 0 ? 0 : _frags[^1].End - _frags[0].Start; } }

    public void Add(Fragment f)
    {
        lock (_gate)
        {
            _frags.Add(f);
            _bytes += f.Bytes.Length;
            while (_frags.Count > 1 &&
                   (_frags[^1].End - _frags[0].End > MaxSeconds || _bytes > MaxBytes))
            {
                _bytes -= _frags[0].Bytes.Length;
                _frags.RemoveAt(0);
            }
        }
        FragmentAdded?.Invoke(f);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _frags.Clear();
            _bytes = 0;
        }
    }

    /// <summary>
    /// Fragments overlapping [from, to). If the range spans an ffmpeg restart (different init segments),
    /// only the newest session is returned and <paramref name="truncated"/> is set.
    /// </summary>
    public List<Fragment> GetRange(double from, double to, out bool truncated)
    {
        truncated = false;
        List<Fragment> hits;
        lock (_gate)
        {
            hits = _frags.Where(f => f.End > from && f.Start < to).ToList();
        }
        if (hits.Count == 0) return hits;

        var lastSession = hits[^1].SessionId;
        if (hits.Any(f => f.SessionId != lastSession))
        {
            truncated = true;
            hits = hits.Where(f => f.SessionId == lastSession).ToList();
        }
        return hits;
    }

    /// <summary>Waits until a fragment ending at or after <paramref name="time"/> has arrived.</summary>
    public async Task<bool> WaitForCoverageAsync(double time, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (LatestEnd is { } end && end >= time) return true;
            await Task.Delay(40, ct).ConfigureAwait(false);
        }
        return LatestEnd is { } e2 && e2 >= time;
    }

    /// <summary>Approximate memory needed for a given clip length at a bitrate.</summary>
    public static long EstimateBytes(double seconds, int bitrateKbps) => (long)(seconds * bitrateKbps * 1000 / 8);
}
