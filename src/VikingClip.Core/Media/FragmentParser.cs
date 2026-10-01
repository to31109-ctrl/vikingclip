using System.Buffers.Binary;
using System.Text;
using VikingClip.Core.Logging;

namespace VikingClip.Core.Media;

/// <summary>
/// Streaming parser for the fragmented MP4 ffmpeg writes to stdout. Emits the init segment (ftyp+moov)
/// once and then one <see cref="Fragment"/> per moof+mdat pair, with timing read from tfdt/trun.
/// </summary>
public sealed class FragmentParser
{
    private byte[] _buf = new byte[1 << 20];
    private int _len;

    private byte[]? _ftyp;
    private byte[]? _pendingMoof;
    private double _pendingStart, _pendingDuration;
    private int _pendingSamples;

    public InitSegment? Init { get; private set; }
    public int SessionId { get; }
    public double SessionOffset { get; }
    public long FragmentsEmitted { get; private set; }

    public event Action<InitSegment>? InitReceived;
    public event Action<Fragment>? FragmentReceived;

    public FragmentParser(int sessionId, double sessionOffset)
    {
        SessionId = sessionId;
        SessionOffset = sessionOffset;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        if (_len + data.Length > _buf.Length)
        {
            var grown = new byte[Math.Max(_buf.Length * 2, _len + data.Length)];
            Buffer.BlockCopy(_buf, 0, grown, 0, _len);
            _buf = grown;
        }
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;

        var pos = 0;
        while (_len - pos >= 8)
        {
            var span = _buf.AsSpan(pos, _len - pos);
            long size = BinaryPrimitives.ReadUInt32BigEndian(span);
            var header = 8;
            if (size == 1)
            {
                if (span.Length < 16) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(span[8..]);
                header = 16;
            }
            if (size < header)
            {
                Log.Warn($"MP4 stream corrupt (box size {size}); resetting parser buffer");
                _len = 0;
                return;
            }
            if (span.Length < size) break; // wait for the rest of this box

            var type = Encoding.ASCII.GetString(span.Slice(4, 4));
            HandleBox(type, span[..(int)size], header);
            pos += (int)size;
        }

        if (pos > 0)
        {
            Buffer.BlockCopy(_buf, pos, _buf, 0, _len - pos);
            _len -= pos;
        }
    }

    private void HandleBox(string type, ReadOnlySpan<byte> box, int header)
    {
        switch (type)
        {
            case "ftyp":
                _ftyp = box.ToArray();
                break;

            case "moov":
            {
                ParseMoov(box[header..], out var timescale, out var defaultDuration);
                var bytes = new byte[(_ftyp?.Length ?? 0) + box.Length];
                _ftyp?.CopyTo(bytes, 0);
                box.CopyTo(bytes.AsSpan(_ftyp?.Length ?? 0));
                Init = new InitSegment
                {
                    Bytes = bytes,
                    Timescale = timescale == 0 ? 1 : timescale,
                    DefaultSampleDuration = defaultDuration,
                    SessionId = SessionId,
                    SessionOffset = SessionOffset,
                };
                InitReceived?.Invoke(Init);
                break;
            }

            case "moof":
                _pendingMoof = box.ToArray();
                ParseMoof(box[header..], Init?.Timescale ?? 1, Init?.DefaultSampleDuration ?? 0,
                    out _pendingStart, out _pendingDuration, out _pendingSamples);
                break;

            case "mdat":
                if (_pendingMoof is not null && Init is not null)
                {
                    var bytes = new byte[_pendingMoof.Length + box.Length];
                    _pendingMoof.CopyTo(bytes, 0);
                    box.CopyTo(bytes.AsSpan(_pendingMoof.Length));
                    _pendingMoof = null;
                    FragmentsEmitted++;
                    FragmentReceived?.Invoke(new Fragment
                    {
                        Bytes = bytes,
                        Init = Init,
                        RelativeStart = _pendingStart,
                        Duration = _pendingDuration,
                        SampleCount = _pendingSamples,
                    });
                }
                break;

            default:
                break; // sidx, styp, mfra, free...
        }
    }

    // ---- moov -------------------------------------------------------------------------------

    private static void ParseMoov(ReadOnlySpan<byte> body, out uint timescale, out uint defaultDuration)
    {
        timescale = 0;
        defaultDuration = 0;
        foreach (var (type, content) in Children(body))
        {
            if (type == "trak" && timescale == 0)
            {
                foreach (var (t2, c2) in Children(content))
                {
                    if (t2 != "mdia") continue;
                    foreach (var (t3, c3) in Children(c2))
                    {
                        if (t3 != "mdhd") continue;
                        var version = c3[0];
                        timescale = version == 1
                            ? BinaryPrimitives.ReadUInt32BigEndian(c3[20..])
                            : BinaryPrimitives.ReadUInt32BigEndian(c3[12..]);
                    }
                }
            }
            else if (type == "mvex")
            {
                foreach (var (t2, c2) in Children(content))
                {
                    if (t2 == "trex" && defaultDuration == 0)
                        defaultDuration = BinaryPrimitives.ReadUInt32BigEndian(c2[12..]); // after version/flags, track_ID, sample_desc_index
                }
            }
        }
    }

    // ---- moof -------------------------------------------------------------------------------

    private static void ParseMoof(ReadOnlySpan<byte> body, uint timescale, uint trexDefault,
        out double start, out double duration, out int samples)
    {
        start = 0;
        duration = 0;
        samples = 0;
        foreach (var (type, content) in Children(body))
        {
            if (type != "traf") continue;
            ulong baseTime = 0;
            var defaultDuration = trexDefault;
            ulong total = 0;
            var count = 0;
            foreach (var (t2, c2) in Children(content))
            {
                switch (t2)
                {
                    case "tfhd":
                    {
                        var flags = BinaryPrimitives.ReadUInt32BigEndian(c2) & 0xFFFFFF;
                        var p = 8; // version/flags + track_ID
                        if ((flags & 0x01) != 0) p += 8;
                        if ((flags & 0x02) != 0) p += 4;
                        if ((flags & 0x08) != 0) defaultDuration = BinaryPrimitives.ReadUInt32BigEndian(c2[p..]);
                        break;
                    }
                    case "tfdt":
                        baseTime = c2[0] == 1
                            ? BinaryPrimitives.ReadUInt64BigEndian(c2[4..])
                            : BinaryPrimitives.ReadUInt32BigEndian(c2[4..]);
                        break;
                    case "trun":
                    {
                        var flags = BinaryPrimitives.ReadUInt32BigEndian(c2) & 0xFFFFFF;
                        var n = (int)BinaryPrimitives.ReadUInt32BigEndian(c2[4..]);
                        var p = 8;
                        if ((flags & 0x001) != 0) p += 4;
                        if ((flags & 0x004) != 0) p += 4;
                        var perSample = 0;
                        if ((flags & 0x100) != 0) perSample += 4;
                        if ((flags & 0x200) != 0) perSample += 4;
                        if ((flags & 0x400) != 0) perSample += 4;
                        if ((flags & 0x800) != 0) perSample += 4;
                        for (var i = 0; i < n && p + perSample <= c2.Length; i++)
                        {
                            total += (flags & 0x100) != 0 ? BinaryPrimitives.ReadUInt32BigEndian(c2[p..]) : defaultDuration;
                            p += perSample;
                        }
                        count += n;
                        break;
                    }
                }
            }
            start = baseTime / (double)timescale;
            duration = total / (double)timescale;
            samples = count;
            return; // first track only (video)
        }
    }

    private static IEnumerable<(string Type, byte[] Content)> Children(ReadOnlySpan<byte> body)
    {
        var list = new List<(string, byte[])>();
        var pos = 0;
        while (body.Length - pos >= 8)
        {
            var span = body[pos..];
            long size = BinaryPrimitives.ReadUInt32BigEndian(span);
            var header = 8;
            if (size == 1)
            {
                if (span.Length < 16) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(span[8..]);
                header = 16;
            }
            if (size == 0) size = span.Length;
            if (size < header || size > span.Length) break;
            list.Add((Encoding.ASCII.GetString(span.Slice(4, 4)), span[header..(int)size].ToArray()));
            pos += (int)size;
        }
        return list;
    }
}
