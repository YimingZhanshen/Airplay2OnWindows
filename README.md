# AirPlay Receiver for Windows

[中文文档](README_zh.md)

Open-source AirPlay 2 receiver for Windows, supporting **screen mirroring** (with audio) and **audio streaming** from Apple devices. Built with C# and .NET 8.

![Build Status](https://github.com/YimingZhanshen/Airplay2OnWindows/workflows/Build%20and%20Test/badge.svg)

## Features

- **Screen Mirroring** — Mirror your iPhone/iPad/Mac screen to Windows with H.264 video and AAC-ELD audio
- **Auto Video Display** — Video player (ffplay) launches automatically when mirroring starts and closes when mirroring stops. Supports repeated connections without restarting the application.
- **Audio Streaming** — Play music and podcasts via AirPlay (ALAC and AAC codecs)
- **Volume Control** — Remote volume adjustment from your Apple device
- **Auto-Discovery** — Bonjour/mDNS service advertising, your Windows PC appears as an AirPlay receiver automatically

## Quick Start

### Download

Download the latest release from [GitHub Releases](https://github.com/YimingZhanshen/Airplay2OnWindows/releases). The package includes all required dependencies (FFmpeg, libfdk-aac).

### Usage

1. Extract the downloaded package
2. Run the application
3. On your Apple device, open Control Center → Screen Mirroring → select your PC
4. The mirroring window will appear automatically — no manual setup needed!

> **Note**: For audio-only streaming (e.g., music), simply select your PC as the AirPlay output device from any audio app on your Apple device.

### Build from Source

#### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- [FFmpeg](https://github.com/BtbN/FFmpeg-Builds/releases) — `ffplay.exe` in the application directory or PATH
- [libfdk-aac](https://github.com/mstorsjo/fdk-aac) — `libfdk-aac-2.dll` in the application directory (required for screen mirroring audio)

#### Build

```bash
dotnet restore AirPlay.sln
dotnet build AirPlay.sln --configuration Release
```

#### Run

1. Start the application
2. On your Apple device, open Control Center → Screen Mirroring → select your PC
3. The mirroring video window (ffplay) will launch automatically

> **Fallback**: If ffplay is not found, you can manually open a video player:
> ```bash
> ffplay -f h264 -probesize 32768 -analyzeduration 0 -fflags nobuffer+discardcorrupt -flags low_delay -framedrop -avioflags direct -vf setpts=0 \\.\pipe\AirPlayVideo
> ```

## Building libfdk-aac on Windows

The `libfdk-aac-2.dll` is required for decoding AAC-ELD audio during screen mirroring. You can build it from source:

1. Install [MSYS2](https://www.msys2.org/)
2. Open MSYS2 MinGW 64-bit terminal and run:
   ```bash
   pacman -S mingw-w64-x86_64-gcc mingw-w64-x86_64-cmake make
   git clone https://github.com/mstorsjo/fdk-aac.git
   cd fdk-aac
   mkdir build && cd build
   cmake -G "MinGW Makefiles" -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON ..
   cmake --build .
   ```
3. Copy the resulting `libfdk-aac-2.dll` to the application directory

## Architecture

```
Apple Device ──AirPlay──► AirPlay Receiver (.NET 8)
                              │
                    ┌─────────┼─────────┐
                    ▼         ▼         ▼
              Screen Mirror  Audio    Volume
              (H.264+AAC)   (ALAC)   Control
                    │         │
              ┌─────┴───┐    │
              ▼         ▼    ▼
          ffplay     FDK-AAC  NAudio
        (auto-launch) Decoder  DirectSound
```

- **Video**: H.264 stream written to a named pipe, ffplay is auto-launched to display the mirroring window when a device connects
- **Mirroring Audio**: AAC-ELD decoded by native FDK-AAC library via P/Invoke, output through NAudio DirectSound
- **Streaming Audio**: ALAC/AAC decoded and output through NAudio DirectSound

## Credits

Based on open-source AirPlay protocol implementations. Special thanks to:
- [SteeBono/airplayreceiver](https://github.com/SteeBono/airplayreceiver) — Original C# AirPlay receiver
- [UxPlay](https://github.com/FDH2/UxPlay) — AirPlay protocol reference
- [mstorsjo/fdk-aac](https://github.com/mstorsjo/fdk-aac) — FDK-AAC codec library

## Disclaimer

All resources in this repository are written using only open-source projects.
The code and related resources are meant for educational purposes only.
The author does not take any responsibility for the use that will be made of it.

## License

[MIT License](LICENSE)
