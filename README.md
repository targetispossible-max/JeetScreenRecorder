# Jeet Screen Recorder

Beginner-friendly Windows screen recorder (C# / .NET 8 / WPF, FFmpeg bundled).
Everything is recorded locally — nothing is uploaded.

## Build via GitHub (no local setup needed)
1. Push this repo to GitHub (branch `main`).
2. Open the **Actions** tab -> *Build JeetScreenRecorder* -> wait for the green check.
3. Download artifact **JeetScreenRecorder-windows**:
   - `JeetScreenRecorder-win-x64.zip` (portable, contains `JeetScreenRecorder.exe` + `ffmpeg/`)
   - `JeetScreenRecorder-Setup-<version>.exe` (installer)
4. For a public release: `git tag v0.1.0 && git push origin v0.1.0` -> files appear under **Releases**.

## Build locally (optional, Windows + .NET 8 SDK)
    dotnet publish src/JeetScreenRecorder/JeetScreenRecorder.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish

## Note on FFmpeg license
The CI bundles a GPL FFmpeg build (needed for libx264 software fallback). If you distribute
the app publicly, comply with the GPL (provide source/offer for FFmpeg).

## Audio (build 9)
Microphone + system audio are mixed by the app and written to a `.pcm` file next to each video segment.
When a segment ends, ffmpeg joins video (copied) and audio (AAC). If audio is silent, a notice is shown after saving.
Logs: `%APPDATA%\JeetScreenRecorder\Logs\<date>.log` (search for "Audio started" / "Segment ... joined").
Fine-tune lip-sync: add `"AudioSyncOffsetMs": 100` to `%APPDATA%\JeetScreenRecorder\settings.json` (+ = audio later, - = audio earlier).

## Build 12 – webcam you can move and resize
- With *Add to video* ticked, the camera now shows on the screen in a small floating window while recording. Because it is on the screen,
  it is recorded exactly as you see it.
- **Move:** drag it with the left mouse button. **Bigger / smaller:** mouse wheel, the `+` / `−` buttons that appear on hover, or the corner handle `◢`.
  `✕` hides the camera. Size and position are remembered; changing Position / Size in the Webcam card resets them.
- Single *Window* capture cannot see other windows, so there the camera is still added in a fixed corner (not movable).

## Build 11 – what is new
- **Webcam:** tick *Add to video* in the Webcam card. The camera appears as a picture-in-picture (corner and size are selectable, optional mirror).
  Use *Detect* to find cameras and *Test camera* to see a live picture before recording. If the camera cannot be opened
  (used by another app, Windows privacy switch off), recording continues without it and a notice is shown.
- **Better HD quality:** higher bitrates per resolution, extra quality switches for NVIDIA / Intel / AMD hardware encoders, sharper
  down-scaling (Lanczos) and correct HD colours (BT.709). New quality names: Low, Medium, High (HD), Very High (Full HD+), Ultra.
- **Works on every Windows PC:** hardware encoders are tested with a made-up picture (not the screen), then fall back step by step:
  best settings -> basic settings -> compatible frame path -> GDI screen capture -> software x264. Weak PCs (<= 4 CPU threads) use a faster x264 preset.
- **New look:** modern dark theme, toggle switches, styled drop-downs and sliders, two-column layout.
- **Logo:** `src/JeetScreenRecorder/Assets/JeetScreenRecorder.ico` is used for the exe, the window / taskbar and the installer (setup icon + wizard images in `installer/`).
