# SyncWave Audio

SyncWave Audio is a modern Windows desktop app for playing one Windows system-audio stream across multiple Bluetooth or wired render devices with adaptive buffering and per-device latency controls.

## Platform

- Windows 10/11
- .NET 8 WPF
- WASAPI loopback capture and WASAPI shared-mode render
- NAudio for audio capture, buffering, and device output

## What is implemented

- Detects active Windows output devices.
- Lets users select two or more render devices.
- Captures Windows system audio with WASAPI loopback.
- Routes the captured stream to each selected device.
- Maintains per-device buffers and delay compensation.
- Supports manual latency offset, independent volume, mono toggle, enhancement flags, profile persistence, reconnect refresh, live peak visualization, and debug logging.
- Includes calibration tone playback and automatic offset profile updates based on estimated device latency.
- Includes an installer script for Inno Setup.

## Important Windows Bluetooth note

Windows exposes Bluetooth A2DP headphones as audio render endpoints, but it does not expose true over-the-air speaker timing, microphone feedback, or all codec telemetry to normal desktop apps. SyncWave Audio therefore combines:

- WASAPI endpoint timing
- codec-family latency estimates
- manual per-device delay offsets
- adaptive buffer drift correction

For sub-10 ms alignment across unrelated Bluetooth speakers, external acoustic measurement or vendor-specific Bluetooth APIs would be needed. The current architecture leaves room for that by isolating telemetry and calibration in service classes.

## Build

Install the .NET 8 SDK and run:

```powershell
dotnet restore
dotnet build .\SyncWaveAudio.sln -c Release
```

## Run

```powershell
dotnet run --project .\src\SyncWaveAudio\SyncWaveAudio.csproj
```

## Publish

```powershell
.\build.ps1
```

The publish output is written to:

```text
artifacts\publish\SyncWaveAudio
```

## Installer

Install Inno Setup, publish the app, then compile:

```powershell
iscc .\installer\SyncWaveAudio.iss
```

The installer output is written to:

```text
artifacts\installer
```

## Project structure

```text
src\SyncWaveAudio
  Audio       WASAPI capture, render sinks, buffering, drift correction
  Devices     Windows endpoint enumeration and Bluetooth telemetry boundary
  Models      Device, sync, and profile models
  Services    Settings and persisted profiles
  ViewModels  MVVM application state and commands
  Views       WPF UI
installer     Inno Setup installer definition
```

## Next production hardening steps

- Replace heuristic Bluetooth telemetry with vendor-specific or Windows device property lookups where available.
- Add acoustic auto-calibration using a microphone feedback loop.
- Add exclusive-mode render option for lower latency when devices support it.
- Add crash dump capture and rolling log files.
- Add UI automation tests and an audio-loop integration test rig.
