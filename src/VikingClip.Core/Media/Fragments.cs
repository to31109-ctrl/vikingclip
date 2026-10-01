namespace VikingClip.Core.Media;

/// <summary>ftyp+moov of one ffmpeg session. Every fragment of the session points at this.</summary>
public sealed class InitSegment
{
    public required byte[] Bytes { get; init; }
    public required uint Timescale { get; init; }
    public uint DefaultSampleDuration { get; init; }
    public required int SessionId { get; init; }

    /// <summary>Engine time (seconds) corresponding to the ffmpeg session's pts 0 (its RTCTIME epoch).</summary>
    public double SessionOffset { get; set; }

    /// <summary>pts (seconds, session-relative) of the first encoded frame; fragment tfdt values count from it.
    /// Reported by ffmpeg via the metadata filter; until it arrives, an arrival-time estimate is used.</summary>
    public double? FirstFramePts { get; set; }
    public double? EstimatedFirstFramePts { get; set; }

    public double BaseTime => SessionOffset + (FirstFramePts ?? EstimatedFirstFramePts ?? 0);
    public bool HasExactTiming => FirstFramePts.HasValue;
}

/// <summary>One moof+mdat pair: a group of frames starting with a keyframe.</summary>
public sealed class Fragment
{
    public required byte[] Bytes { get; init; }
    public required InitSegment Init { get; init; }
    /// <summary>tfdt / timescale: seconds since the first frame of the session.</summary>
    public required double RelativeStart { get; init; }
    public required double Duration { get; init; }
    public int SampleCount { get; init; }
    /// <summary>Engine time when this fragment was fully received (used to estimate timing before ffmpeg reports it).</summary>
    public double ArrivalTime { get; set; }

    public double Start => Init.BaseTime + RelativeStart;
    public double End => Start + Duration;
    public int SessionId => Init.SessionId;
}
