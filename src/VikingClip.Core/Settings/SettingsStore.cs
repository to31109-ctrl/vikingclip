using System.Text.Json;
using System.Text.Json.Serialization;
using VikingClip.Core.Logging;

namespace VikingClip.Core.Settings;

/// <summary>Loads/saves settings.json atomically and tells listeners when something changed.</summary>
public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? Changed;

    public SettingsStore(string? path = null)
    {
        _path = path ?? Paths.SettingsFile;
        Current = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (s is not null)
                {
                    Normalize(s);
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read settings from {_path}; starting with defaults", ex);
            try { File.Copy(_path, _path + ".broken", overwrite: true); } catch { }
        }
        var fresh = new AppSettings();
        Normalize(fresh);
        return fresh;
    }

    private static void Normalize(AppSettings s)
    {
        s.ClipLengthSeconds = Math.Clamp(s.ClipLengthSeconds, 5, 600);
        s.Capture.Fps = s.Capture.Fps switch { <= 30 => 30, <= 60 => 60, _ => 120 };
        if (string.IsNullOrWhiteSpace(s.ClipsRoot)) s.ClipsRoot = Paths.DefaultClipsRoot;
        s.GameOverrides = new Dictionary<string, GameOverride>(s.GameOverrides, StringComparer.OrdinalIgnoreCase);
        foreach (var c in s.DiscordChannels)
            if (c.MaxUploadMB <= 0) c.MaxUploadMB = 10;
    }

    /// <summary>Apply a mutation and persist. The callback receives a copy; on success it becomes Current.</summary>
    public void Update(Action<AppSettings> mutate)
    {
        AppSettings next;
        lock (_gate)
        {
            next = Current.Clone();
            mutate(next);
            Normalize(next);
            Save(next);
            Current = next;
        }
        Changed?.Invoke(next);
    }

    private void Save(AppSettings s)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }
}
