# Video Viewer

A fast, good-looking video library for Windows. Attach folders, browse every video as a thumbnail card, filter and search instantly, and click to play in a near-full-screen player that handles practically any format — including iPhone `.mov` files (HEVC / H.265, HDR, Dolby Vision, portrait rotation), `.mp4`, `.mkv`, `.avi`, `.wmv`, `.webm`, `.m2ts`, `.flv`, `.3gp` and dozens more.

## Run it

1. Download the [latest release zip](../../releases/latest) **or** grab the repo (green **Code** button → **Download ZIP**).
2. Open the `app` folder.
3. Double-click **`VideoViewer.exe`**.

Nothing to install. Needs 64-bit Windows 10 or 11 (the .NET Framework 4.8 it uses is already built into Windows). If SmartScreen warns about an unknown publisher, click **More info → Run anyway** — the exe isn't code-signed.

> Keep the whole `app` folder together — `VideoViewer.exe` needs the `libvlc` folder and DLLs next to it.

## What it does

- **Attach folders** with **Add folder** (pick several at once) or drag folders onto the window. Subfolders are included.
- **Live sync** — files added, renamed, or deleted in attached folders show up (or disappear) within a second.
- **Thumbnail grid** with duration, format and 4K/HD badges. Thumbnails are cached, so after the first pass everything appears instantly.
- **Filters**: folder, file type, length, landscape/portrait/square, date. **Search** matches file and folder names. **Sort** by date, name, size or length.
- **Player**: play/pause, seek with live scrubbing, ±10 s, previous/next video, volume, speed (0.25×–3×), loop, show in folder, full screen. Controls auto-hide while playing.

### Player shortcuts

| Key | Action |
| --- | --- |
| Space / K | Play / pause |
| ← / → | Back / forward 5 s (Ctrl = 30 s) |
| J | Back 10 s |
| ↑ / ↓ | Volume |
| M | Mute |
| F / F11 / double-click | Full screen |
| Esc | Exit full screen, then close |
| PgUp / PgDn | Previous / next video |
| 0–9 | Jump to 0 %–90 % |
| + / − | Speed |
| L | Loop |
| . | Next frame (while paused) |

### Library shortcuts

Ctrl+F search · Enter plays the first result · Ctrl+O add folder · Ctrl +/− thumbnail size · F5 rescan

## How it works

- **WPF on .NET Framework 4.8** — ships with Windows, so the app starts fast with no runtime download.
- **LibVLC 3** (bundled in `app/libvlc`) decodes everything; hardware decoding is on for playback.
- Thumbnails come from Windows' own thumbnail cache when available, otherwise a frame is decoded with VLC (skipping black frames). Cached in `%LOCALAPPDATA%\VideoViewer\thumbs`.
- The grid is virtualized, so libraries with tens of thousands of videos stay smooth.

## Building from source

The `app/` folder is rebuilt automatically by GitHub Actions on every push to `src/` (see `.github/workflows/build.yml`). To build locally on Windows with the .NET 8 SDK:

```
dotnet build src/VideoViewer/VideoViewer.csproj -c Release
```

Output lands in `src/VideoViewer/bin/Release/net48/`.
