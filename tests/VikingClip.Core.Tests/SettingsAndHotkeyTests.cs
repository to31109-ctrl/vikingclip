using VikingClip.Core.Settings;
using VikingClip.Core.Util;
using Xunit;

namespace VikingClip.Core.Tests;

public class SettingsAndHotkeyTests
{
    [Theory]
    [InlineData("Alt+K", HotkeyModifiers.Alt, 'K')]
    [InlineData("ctrl + shift + F10", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x79)]
    [InlineData("Alt+Num5", HotkeyModifiers.Alt, 0x65)]
    [InlineData("Win+Alt+PrintScreen", HotkeyModifiers.Win | HotkeyModifiers.Alt, 0x2C)]
    public void Hotkey_parses(string text, HotkeyModifiers mods, int vk)
    {
        var h = Hotkey.Parse(text);
        Assert.True(h.IsBound);
        Assert.Equal(mods, h.Modifiers);
        Assert.Equal(vk, h.VirtualKey);
        Assert.Equal(h, Hotkey.Parse(h.ToString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Alt+")]
    [InlineData("Banana")]
    public void Hotkey_rejects_garbage(string text) => Assert.False(Hotkey.Parse(text).IsBound);

    [Fact]
    public void Settings_roundtrip_and_update()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vc-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            Assert.Equal(45, store.Current.ClipLengthSeconds);
            var changed = 0;
            store.Changed += _ => changed++;
            store.Update(s =>
            {
                s.ClipLengthSeconds = 90;
                s.DiscordChannels.Add(new DiscordChannel { Name = "#clips", MaxUploadMB = 50 });
                s.GameOverrides["foo.exe"] = new GameOverride { Name = "Foo" };
            });
            Assert.Equal(1, changed);

            var again = new SettingsStore(path);
            Assert.Equal(90, again.Current.ClipLengthSeconds);
            Assert.Single(again.Current.DiscordChannels);
            Assert.Equal(50, again.Current.DiscordChannels[0].MaxUploadMB);
            Assert.True(again.Current.GameOverrides.ContainsKey("FOO.EXE")); // case-insensitive
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Settings_clamps_values()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vc-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Update(s => { s.ClipLengthSeconds = 99999; s.Capture.Fps = 45; });
            Assert.Equal(600, store.Current.ClipLengthSeconds);
            Assert.Equal(60, store.Current.Capture.Fps);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Webhook_secret_roundtrips_and_is_not_plaintext()
    {
        var ch = new DiscordChannel();
        ch.SetWebhookUrl("https://discord.com/api/webhooks/123/abc");
        Assert.StartsWith("dpapi:", ch.WebhookProtected);
        Assert.DoesNotContain("abc", ch.WebhookProtected);
        Assert.Equal("https://discord.com/api/webhooks/123/abc", ch.GetWebhookUrl());
        Assert.Equal("", SecretProtector.Unprotect("dpapi:not-base64!!"));
    }

    [Theory]
    [InlineData("VALORANT", "VALORANT")]
    [InlineData("Baldur's Gate 3: Deluxe?", "Baldur's Gate 3 Deluxe")]
    [InlineData("  weird///name  ", "weird name")]
    [InlineData("CON", "Game")]
    [InlineData("", "Game")]
    public void Sanitizes_names(string input, string expected) => Assert.Equal(expected, FileNames.Sanitize(input));

    [Fact]
    public void Output_path_avoids_collisions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vc-lib-{Guid.NewGuid():N}");
        try
        {
            var when = new DateTime(2026, 9, 30, 21, 15, 3);
            var p1 = FileNames.BuildOutputPath(root, "VALORANT", "VALORANT", ".mp4", when);
            Assert.Equal(Path.Combine(root, "VALORANT", "VALORANT 2026-09-30 21-15-03.mp4"), p1);
            File.WriteAllText(p1, "x");
            var p2 = FileNames.BuildOutputPath(root, "VALORANT", "VALORANT", ".mp4", when);
            Assert.EndsWith("(2).mp4", p2);
        }
        finally { Directory.Delete(root, true); }
    }
}
