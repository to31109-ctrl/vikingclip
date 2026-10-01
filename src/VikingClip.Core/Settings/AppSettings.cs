using System.Text.Json.Serialization;

namespace VikingClip.Core.Settings;

public enum QualityMode { Auto, Custom }
public enum MonitorMode { All, PrimaryOnly }

public sealed class CaptureSettings
{
    public QualityMode Quality { get; set; } = QualityMode.Auto;
    /// <summary>Custom mode only: vertical resolution cap (0 = native). Auto caps at 1440.</summary>
    public int MaxHeight { get; set; } = 1440;
    public int Fps { get; set; } = 60;
    /// <summary>Custom mode only: 0 = pick automatically from resolution/fps.</summary>
    public int BitrateKbps { get; set; } = 0;
    /// <summary>Force a specific encoder plan id (see EncoderPlan.All). null = probe and pick the best.</summary>
    public string? EncoderOverride { get; set; }
    public MonitorMode Monitors { get; set; } = MonitorMode.All;
    public bool DrawCursor { get; set; } = true;
    public bool ShowRecordingIndicator { get; set; } = true;
}

public sealed class AudioSettings
{
    public bool CaptureDesktop { get; set; } = true;
    public bool CaptureMicrophone { get; set; } = true;
    /// <summary>WASAPI device id; null = Windows default communications/multimedia mic.</summary>
    public string? MicrophoneDeviceId { get; set; }
    public float MicrophoneGain { get; set; } = 1.0f;
    public float DesktopGain { get; set; } = 1.0f;
    /// <summary>Manual A/V sync nudge in milliseconds (positive = audio later).</summary>
    public int OffsetMs { get; set; } = 0;
}

public sealed class HotkeySettings
{
    public string Panel { get; set; } = "Alt+K";
    public string InstantClip { get; set; } = "Alt+F10";
    public string ToggleRecording { get; set; } = "Alt+F9";
    public string Screenshot { get; set; } = "Alt+F1";
}

public sealed class DiscordChannel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Label shown in the UI, e.g. "#clips" or "Private".</summary>
    public string Name { get; set; } = "";
    /// <summary>Webhook URL encrypted with DPAPI (current user). Use SecretProtector to read/write.</summary>
    public string WebhookProtected { get; set; } = "";
    /// <summary>Upload cap of the Discord server (10 MB unboosted, 50 MB level 2, 100 MB level 3).</summary>
    public int MaxUploadMB { get; set; } = 10;
    /// <summary>Optional message text. Placeholders: {game} {user} {kind} {length}.</summary>
    public string MessageTemplate { get; set; } = "{kind} · {game}";
    /// <summary>Optional display name override for the webhook post.</summary>
    public string? PosterName { get; set; }
}

public sealed class GameOverride
{
    public string? Name { get; set; }
    public bool NotAGame { get; set; }
}

/// <summary>Cached encoder probe results so the ~2 s probe only re-runs when GPU/driver/ffmpeg change.</summary>
public sealed class EncoderCache
{
    public string Fingerprint { get; set; } = "";
    /// <summary>adapter index -> encoder plan id</summary>
    public Dictionary<int, string> PlanByAdapter { get; set; } = new();
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public string ClipsRoot { get; set; } = Paths.DefaultClipsRoot;
    public int ClipLengthSeconds { get; set; } = 45;

    public HotkeySettings Hotkeys { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();

    public List<DiscordChannel> DiscordChannels { get; set; } = new();

    /// <summary>exe name (lower-case, with extension) -> override</summary>
    public Dictionary<string, GameOverride> GameOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"local" or a DiscordChannel Id. Used by the instant-clip hotkey and "remember my choice".</summary>
    public string? LastDestination { get; set; }
    public bool RememberDestination { get; set; }

    public bool LaunchAtLogin { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    public bool ShowToasts { get; set; } = true;
    public bool FirstRunCompleted { get; set; }

    public EncoderCache? EncoderCache { get; set; }

    [JsonIgnore] public Hotkey PanelHotkey => Hotkey.Parse(Hotkeys.Panel);
    [JsonIgnore] public Hotkey InstantClipHotkey => Hotkey.Parse(Hotkeys.InstantClip);
    [JsonIgnore] public Hotkey ToggleRecordingHotkey => Hotkey.Parse(Hotkeys.ToggleRecording);
    [JsonIgnore] public Hotkey ScreenshotHotkey => Hotkey.Parse(Hotkeys.Screenshot);

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, SettingsStore.JsonOptions);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions)!;
    }
}
