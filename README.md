# Audio Device Switcher

A modern, Windows 11 Fluent audio device switcher and application volume mixer. 
Easily control per-application volume and seamlessly hotkey-switch between multiple audio devices with a beautiful frosted glass interface.

## Features

- **Modern Fluent UI:** Windows 11 Acrylic blur, sleek sliders, and dark mode.
- **Application Mixer:** Control the volume of individual apps (Chrome, Discord, Games) right from your system tray.
- **Quick Switch:** Seamlessly switch between your headset and speakers using a global hotkey (`Ctrl + Shift + S`).
- **On-Screen Display (OSD):** Beautiful on-screen confirmation whenever your audio device changes.
- **Run at Startup:** Built-in setting to automatically launch with Windows.

## Installation

You can download and run the installer directly from this repository.

1. Download the latest installer: [AudioDeviceSwitcherInstaller.exe](releases/AudioDeviceSwitcherInstaller.exe)
2. Run the installer. It will automatically extract the application, place it in your Local AppData, and create convenient shortcuts on your Desktop and Start Menu.
3. Launch the app and configure your preferred Quick Switch devices from the Settings menu!

## Development

Built with C#, WPF, and WinForms targeting .NET 10.0.

- `AudioDeviceSwitcher/`: The main application.
- `AudioDeviceSwitcherInstaller/`: A lightweight, robust C# installer that packages the compiled single-file executable.
