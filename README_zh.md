# AirPlay 投屏接收器（Windows）

[English](README.md)

基于 C# 和 .NET 8 的开源 AirPlay 2 接收器，支持 **屏幕镜像**（含音频）和 **音频推送** 功能。

![构建状态](https://github.com/YimingZhanshen/Airplay2OnWindows/workflows/Build%20and%20Test/badge.svg)

## 功能特性

- **屏幕镜像** — 将 iPhone/iPad/Mac 的屏幕投射到 Windows 上，支持 H.264 视频和 AAC-ELD 音频
- **音频推送** — 通过 AirPlay 播放音乐和播客（支持 ALAC 和 AAC 编解码器）
- **音量控制** — 支持从 Apple 设备远程调节音量
- **自动发现** — Bonjour/mDNS 服务广播，Windows 电脑自动显示为 AirPlay 接收器

## 快速开始

### 下载

从 [GitHub Actions](https://github.com/YimingZhanshen/Airplay2OnWindows/actions) 的构建产物中下载最新版本。安装包已包含所有依赖（FFmpeg、libfdk-aac）。

### 手动配置

#### 前置条件

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 或更高版本
- [FFmpeg](https://github.com/BtbN/FFmpeg-Builds/releases) — 将 `ffmpeg.exe` 和 `ffplay.exe` 放在程序目录或 PATH 中
- [libfdk-aac](https://github.com/mstorsjo/fdk-aac) — 将 `libfdk-aac-2.dll` 放在程序目录中（屏幕镜像音频所需）

#### 编译

```bash
dotnet restore AirPlay.sln
dotnet build AirPlay.sln --configuration Release
```

#### 运行

1. 启动应用程序
2. 打开视频播放器接收投屏画面：
   ```bash
   ffplay -f h264 -probesize 32 -analyzeduration 0 -fflags nobuffer -flags low_delay \\.\pipe\AirPlayVideo
   ```
3. 在 Apple 设备上，打开控制中心 → 屏幕镜像 → 选择你的电脑

## 在 Windows 上编译 libfdk-aac

屏幕镜像时需要 `libfdk-aac-2.dll` 来解码 AAC-ELD 音频。编译步骤如下：

1. 安装 [MSYS2](https://www.msys2.org/)
2. 打开 MSYS2 MinGW 64 位终端，执行：
   ```bash
   pacman -S mingw-w64-x86_64-gcc mingw-w64-x86_64-cmake make
   git clone https://github.com/mstorsjo/fdk-aac.git
   cd fdk-aac
   mkdir build && cd build
   cmake -G "MinGW Makefiles" -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON ..
   cmake --build .
   ```
3. 将生成的 `libfdk-aac-2.dll` 复制到程序目录中

## 系统架构

```
Apple 设备 ──AirPlay──► AirPlay 接收器 (.NET 8)
                              │
                    ┌─────────┼─────────┐
                    ▼         ▼         ▼
              屏幕镜像       音频      音量
            (H.264+AAC)    (ALAC)     控制
                    │         │
              ┌─────┴───┐    │
              ▼         ▼    ▼
          命名管道   FDK-AAC  NAudio
          (ffplay)   解码器  DirectSound
```

- **视频**：H.264 码流写入命名管道（`\\.\pipe\AirPlayVideo`），由 ffplay 或其他兼容播放器消费
- **镜像音频**：AAC-ELD 通过原生 FDK-AAC 库（P/Invoke）解码，经 NAudio DirectSound 输出
- **推送音频**：ALAC/AAC 解码后通过 NAudio DirectSound 输出

## 致谢

基于开源 AirPlay 协议实现。特别感谢：
- [SteeBono/airplayreceiver](https://github.com/SteeBono/airplayreceiver) — 原始 C# AirPlay 接收器
- [UxPlay](https://github.com/FDH2/UxPlay) — AirPlay 协议参考
- [mstorsjo/fdk-aac](https://github.com/mstorsjo/fdk-aac) — FDK-AAC 编解码器库

## 免责声明

本仓库中的所有资源均使用开源项目编写。
代码及相关资源仅供教育目的使用。
作者不对其使用方式承担任何责任。

## 许可证

[MIT 许可证](LICENSE)
