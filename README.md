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
