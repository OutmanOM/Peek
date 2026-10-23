# Peek

Peek is a lightweight Windows 11 file preview app. It runs in the system tray and opens a preview with Space for supported files selected in File Explorer or on the desktop.

## Requirements

- Windows 11
- Visual Studio 2022/2026 with the Windows App SDK and WinUI workload
- .NET 8 SDK
- WebView2 Runtime (used by the installer experience)

## Build

Open `PeekPreview.slnx` in Visual Studio, restore NuGet packages, and build the `PeekPreview` project for x64. The setup experience is in `SetupExperience`; the Inno Setup script is `installer/Peek.iss`.

## Release updates

Published builds will be distributed from GitHub Releases. The app's in-app update check and the automated release workflow will be documented here once those pieces are configured.
