# WinTubeRelay

A Windows tray app for sending YouTube and YouTube Music playback to a specific audio output device.

WinTubeRelay exists for the case where normal browser playback is not enough:

- you want a small local web API and web UI for remote control from your local network
- you want lightweight transport controls without leaving a browser tab open in the foreground
- you want YouTube audio on one specific output, such as an HDMI monitor, TV, receiver, or dedicated speaker path
- you want that output choice to stay fixed across plays and restarts

In practice, it acts as a small Windows controller around `mpv` and `yt-dlp`. The app takes a YouTube URL, starts playback through `mpv`, forces audio to the selected Windows endpoint, and gives you both a tray menu and a local HTTP control surface.

This version is a tray-resident WinForms app that:

- shows a tray icon with a context menu
- enumerates active Windows audio output devices
- lets you pick the preferred output from the tray menu
- persists the selected output and player settings across restarts in `%AppData%\WinTubeRelay\settings.json`
- launches and controls `mpv` directly over JSON IPC
- plays YouTube and YouTube Music URLs
- supports queueing, pause/resume/toggle, stop, skip, mute/unmute, and volume changes
- supports launch-at-sign-in from the tray menu
- keeps a small recent-play history and a favorites menu
- includes the original-style local web UI and HTTP API on port `8765` by default
- exposes favorites and recents through both the tray menu and the web UI

## Requirements

- Windows 10 or 11
- .NET 10 SDK
- mpv for Windows
- yt-dlp for YouTube playback

## Defaults

The app starts with these defaults, which you can change from the tray menu:

- `mpv`: blank
- `yt-dlp`: blank
- browser cookies: `firefox`
- web UI/API port: `8765`

## Build

```powershell
dotnet restore
dotnet build
dotnet run
```

## Publish

To create a single-file Windows release build:

```powershell
.\publish.ps1
```

That produces:

```text
publish\win-x64\WinTubeRelay.exe
```

## Tray Features

- `Play URL...`
- `Play From Clipboard`
- `Queue URL...`
- `Queue From Clipboard`
- `Open Web UI`
- playback controls
- volume controls
- favorites menu
- recent history menu
- audio output selection
- launch at sign-in toggle
- player settings dialog
- open publish folder shortcut

## Notes

- The tray icon is generated at runtime from repo code so it stays deterministic.
- Audio device enumeration uses `NAudio`.
- `mpv` is started on demand and then controlled over its named pipe.
- Audio output selection is converted from the Windows endpoint ID into the `wasapi/{guid}` form that `mpv --audio-device=help` reports.
- The web UI also includes favorites/recent management, backed by the same persisted settings file as the tray app.
