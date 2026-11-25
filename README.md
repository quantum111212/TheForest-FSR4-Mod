# The Forest — AMD FSR 4 Mod (1-Click Installer)

Adds **AMD FidelityFX Super Resolution 4 (FSR 4.1.2)** upscaling and **NativeAA** to **The Forest** (Unity 5.6 DX11) using a custom BepInEx plugin, a native C++ DX11-to-NGX bridge, and [OptiScaler](https://github.com/optiscaler/OptiScaler).

No third-party DLLs are redistributed in this repository — the 1-click installer automatically downloads the official releases of **BepInEx 5** and **OptiScaler** directly from GitHub, detects your Steam game folder and GPU architecture, configures everything automatically, and installs the plugin.

## Features
- **True temporal FSR 4.1.2 upscaling** with AMD multi-phase Halton(2, 3) projection jitter, reversed-Z scene depth, and motion vectors.
- **Full-resolution HDR post-processing & UI** — Eye Adaptation, Bloom, Color Grading, Depth of Field, Vignette, and HUD all render cleanly at display resolution.
- **Accurate Linear-to-sRGB color preservation** — matches stock brightness, contrast, and saturation when toggling FSR 4 on/off.
- **Preserves gameplay & screen effects** — accurate `WorldToScreenPoint` / reticle alignment, plus full support for pause blur, frost, bleeding, and underwater effects.
- **Automatic GPU configuration** — detects AMD RDNA 4 (FP8), RDNA 3 (INT8), RDNA 2 (INT8), and NVIDIA/other GPUs automatically.
- **1-click install & 1-click clean uninstall**.

## 1-Click Installation
1. Download **`TheForest_FSR4_Mod_v1.0.zip`** from the [Releases](../../releases) page.
2. Extract the ZIP anywhere (e.g. Desktop or Downloads).
3. Double-click **`Install_FSR4.bat`**.
   - Auto-detects your Steam installation of *The Forest* (or asks you to pick the folder if installed elsewhere).
   - Downloads official **BepInEx 5 (x64)** and **OptiScaler** directly from GitHub.
   - Configures `OptiScaler.ini` for your GPU and installs `TheForestFSR4.dll` + `TheForestFSR4Native.dll`.
4. Launch *The Forest* normally.

## 1-Click Uninstall
Double-click **`Uninstall_FSR4.bat`** to cleanly remove all mod files and return *The Forest* to vanilla.

## In-Game Hotkeys
| Key | Action |
| --- | --- |
| **`F9`** | Toggle FSR 4 ON / OFF live |
| **`F10`** | Cycle Quality Preset (`NativeAA` 1.0x, `UltraQuality` 1.3x, `Quality` 1.5x, `Balanced` 1.7x, `Performance` 2.0x, `UltraPerformance` 3.0x) |
| **`Insert`** | Open OptiScaler overlay (RCAS sharpness, FSR 4 model options, watermark toggle, stats) |

## Configuration
- Plugin config: `<The Forest>\BepInEx\config\com.theforest.fsr4.cfg`
- OptiScaler config: `<The Forest>\OptiScaler.ini`

## Requirements
- Windows 10 / 11 (64-bit) with a DirectX 11 & DirectX 12 capable GPU
- Internet connection during installation (to download official BepInEx and OptiScaler releases)
- Recent AMD Adrenalin driver for FSR 4 (`amdxcffx64.dll` / INT8 / FP8 support)
- *Single-player / private co-op use — do not use upscaler injectors in anti-cheat multiplayer games.*

## Source Code & Architecture
- `source/TheForestFSR4.cs` — BepInEx 5 C# plugin (Unity 5.6 / Mono 2.0) hooking `PostProcessingBehaviour`, managing scaled HDR render targets, Halton projection jitter, depth/MV capture, and display-resolution HDR blitting.
- `source/TheForestFSR4Native.cpp` — Native x64 C++ DX11 render-thread bridge invoking OptiScaler's `NVSDK_NGX_D3D11_*` API.
- `scripts/install.ps1` & `scripts/uninstall.ps1` — PowerShell 1-click installer & uninstaller.

## Credits
- **[OptiScaler](https://github.com/optiscaler/OptiScaler)** (GPLv3)
- **[BepInEx 5](https://github.com/BepInEx/BepInEx)** (LGPL-2.1)
- **[FakeNVAPI](https://github.com/FakeMichau/fakenvapi)** (MIT)
- **AMD FidelityFX SDK 4.1.2** (fetched directly from OptiScaler's official release)
