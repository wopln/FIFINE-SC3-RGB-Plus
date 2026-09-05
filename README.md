# FIFINE SC3 RGB+

FIFINE SC3 RGB+ is a Windows application that adds customizable RGB lighting
control and Custom Button Shortcuts to the FIFINE AmpliGame SC3 mixer.

![FIFINE SC3 RGB+ application](docs/app-screenshot.png)

## Features

- Custom RGB colors, color picker, and brightness control
- Saved presets with reusable names and settings
- Static, Breathing, Rainbow, Pulse, and Color Cycle effects
- Adjustable 1–100% speed for animated effects, remembered per effect
- Custom A/B/C/D application shortcuts for `.exe` and `.lnk` targets
- Background shortcut engine with Windows system tray controls
- Integrated Settings for application updates, mixer firmware updates, and troubleshooting
- Automatic application updates through GitHub Releases
- Safe Restore Original Firmware and SC3 Recovery Mode support
- Automatic SC3 detection, reconnect handling, and Start with Windows

## Supported hardware

FIFINE AmpliGame SC3. The included RGB+ firmware modification is intended only
for the supported SC3 device profile.

## Installation

1. Download `FIFINE-SC3-RGB-Plus-2.5.0-Setup.exe` from [Releases](../../releases).
2. Install FIFINE SC3 RGB+.
3. Connect the FIFINE SC3.
4. If RGB+ firmware is not installed, use the in-app firmware setup action. New Stock V22 devices install the current RGB+ Firmware 1.5 directly.

Custom Button Shortcuts require **RGB+ Firmware 1.5**. If you already use RGB+
Firmware 1.4, open **Settings → Updates** and choose **Update Firmware**. The
application update and mixer firmware update are separate operations; mixer
firmware is never flashed automatically.

Normal users do not need to download MVA files or use a vendor updater manually.
Do not disconnect the SC3 while firmware setup, update, or restoration is in progress.

## Firmware and recovery

**Settings → Updates** shows both the application version and connected mixer
firmware version. RGB+ Firmware 1.4 can update directly to Firmware 1.5, and
supported Stock V22 can install Firmware 1.5 directly without an intermediate
Firmware 1.4 installation.

**Settings → Troubleshooting → Restore Original Firmware** restores validated
Stock V22 firmware, removes RGB+ support, and returns the app to **RGB setup
required**. If the SC3 is already in Recovery Mode, the app detects it and
offers the same safe restore path.

Firmware redistribution scope is documented in
[FIRMWARE_PERMISSION.md](FIRMWARE_PERMISSION.md).

## Build

Requirements: Windows, .NET 8 SDK, and Inno Setup 6.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

The script validates the production firmware package, builds the Windows app and
native updater, creates the installer, and generates the update manifest and
SHA-256 checksum.

## License

Project source code is available under the [MIT License](LICENSE). The bundled
modified SC3 firmware is distributed under the documented permission and is not
claimed as MIT-licensed project source.

## Unofficial community project

FIFINE SC3 RGB+ is an unofficial community project and is not affiliated with
or endorsed by FIFINE. FIFINE and AmpliGame are trademarks of their respective
owner.