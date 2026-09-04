# FIFINE SC3 RGB+

FIFINE SC3 RGB+ is a Windows application that adds customizable RGB lighting
control to the FIFINE AmpliGame SC3 mixer.

![FIFINE SC3 RGB+ application](docs/app-screenshot.png)

## Features

- Custom RGB colors, color picker, and brightness control
- Saved presets with reusable names and settings
- Static, Breathing, Rainbow, Pulse, and Color Cycle effects
- Adjustable 1–100% speed for animated effects, remembered per effect
- Lighting On/Off and original SC3 button/status lighting preservation
- Integrated Settings for updates and troubleshooting
- Automatic application updates through GitHub Releases
- Safe Restore Original Firmware and SC3 Recovery Mode support
- Automatic SC3 detection, reconnect handling, and Start with Windows

## Supported hardware

FIFINE AmpliGame SC3. The included RGB firmware modification is intended only
for the supported SC3 device profile.

## Installation

1. Download `FIFINE-SC3-RGB-Plus-2.4.0-Setup.exe` from [Releases](../../releases).
2. Install FIFINE SC3 RGB+.
3. Connect the FIFINE SC3.
4. Open the application and select **Enable RGB Control** if RGB setup is required.

The app handles firmware setup and recovery from within the application. Normal
users do not need to select MVA files or use a vendor updater manually. Do not
disconnect the SC3 while firmware setup or restoration is in progress.

## Firmware and recovery

**Enable RGB Control** installs the validated RGB+ firmware only after package
validation. **Settings → Troubleshooting → Restore Original Firmware** restores
validated Stock V22 firmware, removes RGB+ support, and returns the app to
**RGB setup required**. If the SC3 is already in Recovery Mode, the app detects
it and offers the same safe restore path.

Firmware redistribution scope is documented in
[FIRMWARE_PERMISSION.md](FIRMWARE_PERMISSION.md).

## Build

Requirements: Windows, .NET 8 SDK, and Inno Setup 6.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

The script builds the Windows app, native updater, installer, update manifest,
and SHA-256 checksum from this repository tree.

## License

Project source code is available under the [MIT License](LICENSE). The bundled
modified SC3 firmware is distributed under the documented permission and is not
claimed as MIT-licensed project source.

## Unofficial community project

FIFINE SC3 RGB+ is an unofficial community project and is not affiliated with
or endorsed by FIFINE. FIFINE and AmpliGame are trademarks of their respective
owner.