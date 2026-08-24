<div align="center">
  
# 🎧 Audio Device Switcher

**A modern, Windows 11 Fluent audio device switcher and per-application volume mixer.**

[![Platform](https://img.shields.io/badge/Platform-Windows%2011-0078d4?logo=windows&logoColor=white)](#)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512bd4?logo=dotnet&logoColor=white)](#)
[![Release](https://img.shields.io/github/v/release/Kwan-desu/AudioDeviceSwitcher?color=2ea44f)](https://github.com/Kwan-desu/AudioDeviceSwitcher/releases/latest)

[Download Latest Release](#installation) • [Report Bug](https://github.com/Kwan-desu/AudioDeviceSwitcher/issues) • [Request Feature](https://github.com/Kwan-desu/AudioDeviceSwitcher/issues)

</div>

---

## ✨ Features

- **🖌️ Modern Fluent UI:** True Windows 11 Acrylic blur, custom-styled translucent sliders, and native dark mode integration.
- **🎛️ Application Mixer:** Control the volume of individual apps (Chrome, Discord, Games) right from your system tray without opening bloated Windows settings.
- **⚡ Quick Switch:** Seamlessly bounce between your headset and speakers using a customizable global hotkey (`Ctrl + Shift + S`).
- **🖥️ On-Screen Display (OSD):** Beautiful, non-intrusive on-screen confirmation whenever your audio device changes.
- **🚀 Run at Startup:** Built-in settings to automatically launch silently with Windows.

---

## 🚀 Getting Started

### Installation

You can download and run the standalone installer directly from the releases page:

1. Download the latest installer: **[AudioDeviceSwitcherInstaller.zip](https://github.com/Kwan-desu/AudioDeviceSwitcher/releases/latest/download/AudioDeviceSwitcherInstaller.zip)**
2. Extract the zip file and run `AudioDeviceSwitcherInstaller.exe`. 
3. The installer will automatically extract the application, place it in your Local AppData, and create convenient shortcuts on your Desktop and Start Menu.
4. Launch the app from your System Tray and configure your preferred Quick Switch devices from the Settings menu!

> **Note:** The application minimizes to the System Tray by default. **Left-click** the tray icon to quickly swap devices, or **Right-click** it to access the Volume Mixer and Settings.

---

## 🛠️ Building from Source

If you'd like to build the project yourself, ensure you have the **.NET 10.0 SDK** installed.

```bash
# Clone the repository
git clone https://github.com/Kwan-desu/AudioDeviceSwitcher.git

# Navigate to the source directory
cd AudioDeviceSwitcher/AudioDeviceSwitcher

# Build and publish as a single-file executable
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

---

## 🏗️ Architecture

Built with C#, WPF, and WinForms targeting `.NET 10.0` to leverage modern APIs while remaining incredibly lightweight.

- `AudioDeviceSwitcher/`: The core application handling UI, audio routing (via CoreAudio APIs), and Win32 global keyboard hooks.
- `AudioDeviceSwitcherInstaller/`: A robust, custom-built C# installer that packages the compiled executable for seamless user deployments.

---

## 🤝 Contributing

Contributions, issues, and feature requests are always welcome! Feel free to check the [issues page](https://github.com/Kwan-desu/AudioDeviceSwitcher/issues) to get involved.

## 📜 License

This project is open-source and free to use.
