# VikingClip

Press **Alt+K** in any game. Clip the last 45 seconds, start a recording, or take a screenshot - then drop it in a Discord channel or keep it on your PC. Made for a group of friends; works on any Windows 10/11 PC with any GPU.

## What it does

- **Alt+K panel** anywhere, including fullscreen games: **Clip** · **Record** · **Screenshot**.
- **Replay buffer** that never re-encodes: the last N seconds (default 45, up to 10 minutes) already sit in RAM as video, so a clip is saved instantly.
- **Game folders**: clips land in `Videos\VikingClip\<Game>\`. Games are recognised from Steam, Epic, Riot and a built-in list; anything else goes to `Desktop`.
- **Discord**: add as many channels as you like (each is a webhook). Pick one in the panel; a copy is compressed to fit the server's upload limit (10 MB without boosts). Private channel = private clips.
- **Dual monitors**: every monitor is buffered; you clip the one you are on.
- **Audio**: desktop audio + microphone, saved as three tracks (mix, desktop, mic).
- **Light on FPS**: frames stay on the GPU and go straight to the hardware encoder (NVIDIA NVENC, AMD AMF, Intel Quick Sync). No code is injected into games, so no anti-cheat trouble.
- **Installs like an app**: Start-menu entry, starts with Windows, updates itself from this repo's releases.

## Install

1. Download `VikingClip-win-Setup.exe` from the [latest release](https://github.com/to31109-ctrl/vikingclip/releases/latest).
2. Run it. Windows may show "Windows protected your PC" because the installer is not signed with a paid certificate: click **More info → Run anyway**.
3. VikingClip opens, starts capturing, and from now on starts with Windows (tray icon). Press **Alt+K**.

Updates are automatic: when a new release is published here, every install downloads it in the background and applies it on the next restart of the app.

## Using it

| Hotkey (default) | What it does |
|---|---|
| **Alt+K** | Opens the panel: Clip · Record · Screenshot, then where to save. Keys `1-3` pick, `Esc` closes. |
| Alt+F10 | Instant clip to your last-used destination (no panel). |
| Alt+F9 | Start / stop a recording of the monitor you are on. |
| Alt+F1 | Screenshot to your last-used destination. |

All hotkeys can be changed in **Hotkeys**.

**Exclusive-fullscreen games** (older games with real exclusive fullscreen): the panel cannot draw inside the game without injecting code, so VikingClip alt-tabs the game out to show the panel and hands focus back when you are done. The clip is cut at the moment you pressed Alt+K, so it never contains the alt-tab. Borderless/windowed games (most modern games) just get the panel on top.

### Discord channels

1. In Discord: channel settings → **Integrations → Webhooks → New Webhook → Copy Webhook URL**.
2. In VikingClip: **Discord → Add a channel**, give it a name, paste the URL, choose the server's upload limit (10 MB unless boosted).
3. The channel now appears in the Alt+K panel. For private clips, make a private channel and add a webhook there.

The webhook URL works like a password for that channel. VikingClip stores it encrypted for your Windows account and only ever sends it to Discord.

### Settings worth knowing

- **Capture → Clip length**: 5 s to 10 min. RAM use is shown next to it.
- **Capture → Quality**: *Auto* records at native resolution on the GPU encoder. *Custom* lets you cap resolution/bitrate (downscaling needs a CPU copy of each frame).
- **Audio**: desktop/mic on or off, mic device, volumes, and an A/V sync nudge.
- **Storage & games**: clips folder, start with Windows, per-exe game name overrides.
- **About**: diagnostics, log folder, update status.

## Requirements and limits

- Windows 10 (2004+) or Windows 11, 64-bit. Nothing else to install.
- Any GPU. A hardware encoder is used when available; otherwise a CPU encoder with a warning.
- Discord webhooks are capped by the server's boost level (10 / 50 / 100 MB). A 45 s clip is compressed to about 720p30 to fit 10 MB.
- HDR desktops are captured in SDR (colours may look flat in clips).
- The REC badge, panel and notifications are excluded from captures.

## Contributing

Anyone in the group can change VikingClip from their own PC.

```bash
git clone https://github.com/to31109-ctrl/vikingclip.git
cd vikingclip
powershell -ExecutionPolicy Bypass -File tools/get-ffmpeg.ps1   # once: fetches the pinned ffmpeg (~185 MB)
dotnet build
dotnet test
dotnet run --project src/VikingClip.App
```

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Visual Studio 2022 or Rider open `VikingClip.sln` directly.

- **Add a game** to the folder list: edit [`src/VikingClip.Core/Games/known-games.json`](src/VikingClip.Core/Games/known-games.json) (exe name → game name) and open a pull request.
- **Release a new version**: push a tag like `v0.2.0` (or run the *Release* workflow under Actions and type the version). GitHub Actions builds the installer, publishes it, and every install updates itself. Versions must look like `1.2.3`.
- Design notes and the capture pipeline are described in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## License

GPL-3.0-or-later. Bundles [FFmpeg](https://ffmpeg.org) (GPL build by [BtbN](https://github.com/BtbN/FFmpeg-Builds)), [NAudio](https://github.com/naudio/NAudio), [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) and [Velopack](https://velopack.io).
