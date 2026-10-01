using VikingClip.Core.Capture;
using VikingClip.Core.Discord;
using VikingClip.Core.Games;
using VikingClip.Core.Settings;
using Xunit;

namespace VikingClip.Core.Tests;

public class GamesAndDiscordTests
{
    [Fact]
    public void Known_games_resource_loads()
    {
        var k = KnownGames.Load();
        Assert.True(k.Games.Count > 100);
        Assert.Equal("VALORANT", k.Games["valorant-win64-shipping.exe"]);
        Assert.Contains("explorer.exe", k.NotGames);
        Assert.NotEmpty(k.TitleRules);
    }

    [Theory]
    [InlineData("FortniteClient-Win64-Shipping", "Fortnite")]
    [InlineData("RocketLeague", "Rocket League")]
    [InlineData("my_cool_game_x64", "my cool")]
    [InlineData("TslGame", "Tsl")]
    public void Cleans_exe_stems(string stem, string expected) => Assert.Equal(expected, GameDetector.CleanExeStem(stem));

    [Fact]
    public void Classification_order_override_known_title_denylist_heuristic()
    {
        var overrides = new Dictionary<string, GameOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome.exe"] = new() { Name = "Chrome Dino" },
            ["cs2.exe"] = new() { NotAGame = true },
        };
        var d = new GameDetector(() => overrides, KnownGames.Load(), new LauncherLibraries());

        Assert.Equal("Chrome Dino", d.Classify("chrome.exe", @"C:\x\chrome.exe", "", 0, true).Name);
        Assert.False(d.Classify("cs2.exe", @"C:\x\cs2.exe", "", 0, true).IsGame);
        Assert.Equal("VALORANT", d.Classify("VALORANT-Win64-Shipping.exe", @"D:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe", "VALORANT", 0, true).Name);
        Assert.Equal("Minecraft", d.Classify("javaw.exe", @"C:\java\javaw.exe", "Minecraft 1.21", 0, false).Name);
        Assert.False(d.Classify("javaw.exe", @"C:\java\javaw.exe", "IntelliJ", 0, true).IsGame);
        Assert.False(d.Classify("explorer.exe", @"C:\Windows\explorer.exe", "", 0, true).IsGame);
        var unknownWindowed = d.Classify("someapp.exe", Path.Combine(Path.GetTempPath(), "someapp.exe"), "", 0, false);
        Assert.False(unknownWindowed.IsGame);
        Assert.Equal("Desktop", unknownWindowed.FolderName);
        var unknownFullscreen = d.Classify("CoolShooter-Win64-Shipping.exe", @"C:\nope\CoolShooter-Win64-Shipping.exe", "", 0, true);
        Assert.True(unknownFullscreen.IsGame);
        Assert.Equal("Cool Shooter", unknownFullscreen.Name);
    }

    [Fact]
    public void Parses_steam_manifests()
    {
        var acf = """
            "AppState"
            {
            	"appid"		"730"
            	"name"		"Counter-Strike 2"
            	"installdir"		"Counter-Strike Global Offensive"
            	"UserConfig" { "name" "ignored" }
            }
            """;
        var parsed = LauncherLibraries.ParseAppManifest(acf);
        Assert.Equal(("Counter-Strike 2", "Counter-Strike Global Offensive"), parsed);

        var vdf = """
            "libraryfolders"
            {
            	"0" { "path" "C:\\Program Files (x86)\\Steam" }
            	"1" { "path" "D:\\SteamLibrary" }
            }
            """;
        Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, LauncherLibraries.ParseLibraryFolders(vdf));
    }

    [Fact]
    public void Parses_epic_items_and_skips_engines()
    {
        var item = """{ "DisplayName": "Fortnite", "InstallLocation": "D:/Epic/Fortnite", "AppCategories": ["public","games","applications"] }""";
        var g = LauncherLibraries.ParseEpicItem(item);
        Assert.Equal("Fortnite", g!.Name);
        Assert.Equal(@"D:\Epic\Fortnite", g.Directory);
        Assert.Null(LauncherLibraries.ParseEpicItem("""{ "DisplayName": "UE_5.4", "InstallLocation": "C:/UE", "AppCategories": ["engines"] }"""));
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/123456/abc_DEF-ghi", true)]
    [InlineData("https://discordapp.com/api/webhooks/123456/abc", true)]
    [InlineData("https://canary.discord.com/api/v10/webhooks/123456/abc?wait=true", true)]
    [InlineData("https://discord.com/channels/1/2", false)]
    [InlineData("not a url", false)]
    public void Validates_webhook_urls(string url, bool ok)
    {
        Assert.Equal(ok, DiscordWebhook.TryNormalizeUrl(url, out var n));
        if (ok) Assert.StartsWith("https://discord.com/api/webhooks/123456/", n);
    }

    [Fact]
    public void Compression_plan_fits_limit()
    {
        var limit = 10L * 1024 * 1024;
        var p = DiscordCompressor.MakePlan(45, limit, 1920, 1080, 60);
        Assert.True(p.EstimatedBytes(45) <= limit, $"{p.EstimatedBytes(45)} > {limit}");
        Assert.Equal(540, p.Height);   // resolution gives way, frame rate stays
        Assert.Equal(960, p.Width);
        Assert.Equal(60, p.Fps);
        Assert.InRange(p.VideoKbps, 1200, 1800);

        var big = DiscordCompressor.MakePlan(45, 100L * 1024 * 1024, 2560, 1440, 60);
        Assert.Equal(1080, big.Height);
        Assert.Equal(60, big.Fps);
        Assert.Equal(1920, big.Width);

        // Ten minutes cannot fit in 10 MB at a watchable bitrate: the plan bottoms out at the floor.
        var longRec = DiscordCompressor.MakePlan(600, limit, 1920, 1080, 60);
        Assert.Equal(480, longRec.Height);
        Assert.Equal(60, longRec.Fps);
        Assert.Equal(150, longRec.VideoKbps);
        Assert.Equal(64, longRec.AudioKbps);
    }

    [Fact]
    public void Auto_bitrate_scales_with_resolution()
    {
        Assert.Equal(40000, FfmpegArgs.AutoBitrateKbps(1920, 1080, 60));
        Assert.InRange(FfmpegArgs.AutoBitrateKbps(2560, 1440, 60), 69000, 73000);
        Assert.InRange(FfmpegArgs.AutoBitrateKbps(1920, 1080, 30), 25000, 28000);
        Assert.Equal(8000, FfmpegArgs.AutoBitrateKbps(640, 480, 30)); // clamp floor
        Assert.Equal(120000, FfmpegArgs.AutoBitrateKbps(3840, 2160, 60)); // clamp ceiling
    }

    [Fact]
    public void Output_size_caps_height_and_keeps_even()
    {
        Assert.Equal((2560, 1440), EncoderPlan.OutputSize(2560, 1440, 1440));
        Assert.Equal((2560, 1440), EncoderPlan.OutputSize(3840, 2160, 1440));
        Assert.Equal((1920, 1080), EncoderPlan.OutputSize(1920, 1080, 0));
        Assert.Equal((3440, 1440), EncoderPlan.OutputSize(3440, 1440, 1440));
        Assert.Equal((1720, 720), EncoderPlan.OutputSize(3440, 1440, 720));
    }

    [Fact]
    public void Mux_args_cover_track_layouts()
    {
        var both = FfmpegArgs.Mux("v.mp4", "d.pcm", "m.pcm", 0, 45, 1f, 1.5f, "out.mp4");
        Assert.Contains("amix=inputs=2", string.Join(" ", both));
        Assert.Equal(3, both.Count(a => a == "-map") - 1); // video + mix + desktop + mic = 4 maps
        Assert.Contains("title=Microphone", both);
        Assert.Contains("volume=1.5", string.Join(" ", both));

        var desktopOnly = FfmpegArgs.Mux("v.mp4", "d.pcm", null, 2.5, 10, 1f, 1f, "out.mp4");
        Assert.DoesNotContain("amix", string.Join(" ", desktopOnly));
        Assert.Contains("title=Desktop", desktopOnly);
        Assert.Contains("2.5", desktopOnly);

        var videoOnly = FfmpegArgs.Mux("v.mp4", null, null, 0, 10, 1f, 1f, "out.mp4");
        Assert.DoesNotContain("-c:a", videoOnly);
        Assert.Contains("copy", videoOnly);
    }

    [Fact]
    public void Capture_args_are_well_formed()
    {
        var m = new Display.MonitorInfo(0, 0, "GPU", 0x10DE, @"\\.\DISPLAY1", IntPtr.Zero, new Native.RECT { Right = 3840, Bottom = 2160 }, true);
        var args = FfmpegArgs.Capture(m, EncoderPlan.Nvenc, new CaptureParams(60, 20000, 1440, true), 1_700_000_000_000_000, gpuScalerAvailable: true);
        var joined = string.Join(" ", args);
        Assert.Contains("ddagrab=output_idx=0:framerate=60:draw_mouse=1", joined);
        Assert.Contains("setpts=(RTCTIME-1700000000000000)/(TB*1000000)", joined);
        Assert.Contains("scale_d3d11=width=2560:height=1440", joined);
        Assert.Contains("metadata=mode=add", joined);
        Assert.Contains("h264_nvenc", args);
        Assert.Contains("smpte170m", args);
        Assert.Contains("pipe:1", args);
        Assert.Equal("-", args[^1]);

        var sw = FfmpegArgs.Capture(m, EncoderPlan.X264, new CaptureParams(30, 8000, 1080, false), 1, gpuScalerAvailable: false);
        var sj = string.Join(" ", sw);
        Assert.Contains("hwdownload,format=bgra,scale=w=1920:h=1080:out_color_matrix=bt709:out_range=tv,format=nv12", sj);
        Assert.Contains("bt709", sw);
        Assert.DoesNotContain("smpte170m", sw);
    }
}
