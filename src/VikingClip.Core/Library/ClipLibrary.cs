using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VikingClip.Core.Capture;
using VikingClip.Core.Logging;
using VikingClip.Core.Media;

namespace VikingClip.Core.Library;

public enum MediaKind { Clip, Recording, Screenshot }

public sealed class ClipMeta
{
    public MediaKind Kind { get; set; }
    public string Game { get; set; } = "";
    public double DurationSeconds { get; set; }
    public string? Monitor { get; set; }
    public string? Encoder { get; set; }
    public DateTime CreatedLocal { get; set; }
}

public sealed class ClipEntry
{
    public required string Path { get; init; }
    public required ClipMeta Meta { get; init; }
    public required DateTime Created { get; init; }
    public required long Bytes { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Game => Meta.Game;
    public string? ThumbnailPath { get; set; }
}

/// <summary>Knows what has been saved: scans the clips folder and keeps a small index with durations etc.</summary>
public sealed class ClipLibrary
{
    private readonly string _indexPath;
    private readonly Dictionary<string, ClipMeta> _index;
    private readonly object _gate = new();

    public ClipLibrary(string? indexPath = null)
    {
        _indexPath = indexPath ?? Paths.LibraryIndexFile;
        _index = Load();
    }

    private Dictionary<string, ClipMeta> Load()
    {
        try
        {
            if (File.Exists(_indexPath))
                return JsonSerializer.Deserialize<Dictionary<string, ClipMeta>>(File.ReadAllText(_indexPath), Json) ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) { Log.Warn("Library index unreadable, rebuilding: " + ex.Message); }
        return new Dictionary<string, ClipMeta>(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public void Record(string path, ClipMeta meta)
    {
        lock (_gate)
        {
            _index[path] = meta;
            Save();
        }
    }

    public void Forget(string path)
    {
        lock (_gate)
        {
            if (_index.Remove(path)) Save();
        }
    }

    private void Save()
    {
        try
        {
            var tmp = _indexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_index, Json));
            File.Move(tmp, _indexPath, true);
        }
        catch (Exception ex) { Log.Warn("Could not save library index: " + ex.Message); }
    }

    public List<ClipEntry> Scan(string root)
    {
        var list = new List<ClipEntry>();
        if (!Directory.Exists(root)) return list;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var game = Path.GetFileName(dir);
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".mp4" or ".png")) continue;
                FileInfo fi;
                try { fi = new FileInfo(file); } catch { continue; }
                ClipMeta meta;
                lock (_gate)
                {
                    if (!_index.TryGetValue(file, out meta!))
                    {
                        meta = new ClipMeta
                        {
                            Kind = ext == ".png" ? MediaKind.Screenshot : fi.Name.Contains("Recording", StringComparison.OrdinalIgnoreCase) ? MediaKind.Recording : MediaKind.Clip,
                            Game = game,
                            CreatedLocal = fi.CreationTime,
                        };
                    }
                }
                list.Add(new ClipEntry { Path = file, Meta = meta, Created = fi.CreationTime, Bytes = fi.Length });
            }
        }
        return list.OrderByDescending(e => e.Created).ToList();
    }

    public async Task<string?> EnsureThumbnailAsync(ClipEntry entry, CancellationToken ct = default)
    {
        try
        {
            if (entry.Meta.Kind == MediaKind.Screenshot) return entry.Path;
            var key = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(entry.Path + File.GetLastWriteTimeUtc(entry.Path).Ticks)));
            var thumb = Path.Combine(Paths.ThumbnailsDir, key + ".jpg");
            if (File.Exists(thumb)) return thumb;
            var at = entry.Meta.DurationSeconds > 2 ? Math.Min(1.0, entry.Meta.DurationSeconds / 2) : 0;
            var r = await Ffmpeg.RunAsync(FfmpegArgs.Thumbnail(entry.Path, thumb, at), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (!r.Success && at > 0)
                r = await Ffmpeg.RunAsync(FfmpegArgs.Thumbnail(entry.Path, thumb, 0), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            return r.Success && File.Exists(thumb) ? thumb : null;
        }
        catch (Exception ex)
        {
            Log.Debug("Thumbnail failed: " + ex.Message);
            return null;
        }
    }

    public async Task FillDurationAsync(ClipEntry entry, CancellationToken ct = default)
    {
        if (entry.Meta.Kind == MediaKind.Screenshot || entry.Meta.DurationSeconds > 0) return;
        var d = await Ffmpeg.ProbeDurationAsync(entry.Path, ct).ConfigureAwait(false);
        if (d is { } dur)
        {
            entry.Meta.DurationSeconds = dur;
            Record(entry.Path, entry.Meta);
        }
    }

    public void Delete(ClipEntry entry)
    {
        try { File.Delete(entry.Path); } catch (Exception ex) { Log.Warn("Delete failed: " + ex.Message); throw; }
        if (entry.ThumbnailPath is not null && entry.ThumbnailPath != entry.Path)
        {
            try { File.Delete(entry.ThumbnailPath); } catch { }
        }
        Forget(entry.Path);
    }
}
