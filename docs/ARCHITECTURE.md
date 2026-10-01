# VikingClip architecture

Two projects: `VikingClip.Core` (engine, no UI, unit-tested) and `VikingClip.App` (WPF: tray, panel, settings).

## Capture pipeline

```
 per monitor:  ffmpeg.exe  ──stdout (fragmented MP4)──►  FragmentParser  ──►  FragmentRing (RAM, last N s)
               ddagrab (Desktop Duplication, D3D11 texture)
                 → setpts (wall-clock timestamps)
                 → setparams (colour tags)
                 → h264_nvenc | h264_amf | h264_qsv | libx264
                 → mp4 muxer, 1 keyframe/second, one fragment per keyframe

 audio:        NAudio WASAPI loopback (desktop) + WASAPI capture (mic)  ──►  AudioRing (16-bit 48 kHz stereo, last N s)

 clip:         init segment + fragments overlapping [T-45, T]  →  video.mp4 (no re-encode)
               AudioRing slices for the same window         →  desktop.pcm / mic.pcm
               ffmpeg -c:v copy -c:a aac (3 tracks: mix, desktop, mic)  →  final MP4
```

- `MonitorCapture` owns one ffmpeg process, restarts it with backoff when it dies (display mode change, lock screen) and is watched by a watchdog that kills a stalled process. ffmpeg processes live in a Windows job object, so they die with the app.
- `EncoderProbe` runs a 15-frame capture per GPU adapter with candidate plans (`EncoderPlan.CandidatesFor`) and caches the winner per GPU/driver/ffmpeg fingerprint.
- `RecordingSession` appends fragments to a temp file as they arrive (disk-bound, not RAM-bound) and muxes at stop. If ffmpeg restarted mid-recording the parts are concatenated.
- `ScreenshotGrabber` uses DXGI Desktop Duplication in-process (works for exclusive fullscreen), GDI as fallback.

## Timing model

Everything is stamped on one timeline: seconds since the engine's epoch (`CaptureClock`, Unix microseconds).

- ffmpeg's `setpts=(RTCTIME-X)/(TB*1000000)` writes wall-clock time into frame pts, where X is the session's epoch.
- The MP4 muxer stores fragment times (`tfdt`) *relative to the first frame*, so the absolute time of the first frame is reported once through a one-frame side branch: `split → trim=end_frame=1 → metadata=add → metadata=print` prints `pts_time` to stderr. Until that line arrives, an arrival-time estimate is used.
- Audio chunks are stamped on arrival, counted in samples from an anchor that is slewed slowly towards wall-clock (no clicks), re-anchored after stalls. Gaps are filled with silence on export, so A/V stay aligned.
- A clip ends exactly at the hotkey press (`-t` on the muxed output; B-frames are disabled so cutting trailing P-frames is safe) and starts at the keyframe at or before `T - length` (≤ 1 s early).

## Things learned from probing (keep in mind when changing ffmpeg args)

- NVENC converts RGB input with **BT.601 limited range**, while ffmpeg tags the stream `gbr`/full by default → washed-out colours in players. We tag `smpte170m`/`tv` for the GPU NVENC path and convert explicitly to BT.709 on the CPU-copy paths (`scale=out_color_matrix=bt709:out_range=tv`).
- `-force_key_frames` only produces real keyframes on NVENC with `-forced-idr 1` (`-forced_idr 1` on AMF/QSV).
- `force_key_frames expr:isnan(prev_forced_t)+gte(t,prev_forced_t+1)` is independent of the pts start offset; `gte(t,n_forced)` is not.
- `metadata=mode=print` prints nothing for frames without metadata entries - add one first.
- `scale_d3d11` exists in the bundled ffmpeg but fails to configure on `ddagrab` frames (`Unsupported pixel format` / `Unknown error`), so GPU plans record native resolution; a resolution cap switches to the CPU-copy variant (`EncoderPlan.ScalableVariant`).
- `ddagrab` must run on the adapter that owns the output (`-init_hw_device d3d11va=d3d:<adapterIndex>`), hence the DXGI enumeration in `DisplayEnumerator`.
- Output `-t` is measured from pts 0, not from the first frame - don't use it on sources with offset timestamps (use `-frames:v`).

## App layer

- `HotkeyService`: `RegisterHotKey` on a hidden top-level window (also receives `WM_DISPLAYCHANGE`). No hooks, no injection.
- `ActionController`: hotkey → `Moment` (time, foreground window, game, monitor, screenshot task) → panel → clip/record/screenshot → optional Discord post.
- `PanelWindow`: topmost, takes focus deliberately (keyboard-first; the game is alt-tabbed only if it is truly exclusive fullscreen), restores the previous foreground window on close. Excluded from capture via `WDA_EXCLUDEFROMCAPTURE`, like the toast and REC badge.
- `GameDetector`: overrides → known-games.json → title rules → Steam/Epic/Riot install folders → deny-list → packaged apps → fullscreen heuristic.
- `UpdateService`: Velopack `GithubSource`; checks 60 s after start and every 6 h, downloads, applies on restart or exit.

## Storage

- Settings: `%AppData%\VikingClip\settings.json` (webhook URLs DPAPI-encrypted). Logs: `%AppData%\VikingClip\logs`.
- Clips: `Videos\VikingClip\<Game>\<Game> yyyy-MM-dd HH-mm-ss.mp4|png`, recordings named `<Game> Recording …`.
- Library index with durations/thumbnails: `%AppData%\VikingClip\library.json` + `thumbnails\`.
