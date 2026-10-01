# Celestium Emulator

A lightweight Windows launcher for the official Android Emulator — a replacement for MuMu Player. Run multiple Android instances, install APKs by drag-and-drop, and open logcat/adb shell in one click.

## Features
- One card per Android Virtual Device (AVD) with **Start / Stop** and live status (stopped → booting → running)
- **Install APK…** button, or drag `.apk` files onto the window
- **More ▾**: cold boot, writable-system boot (root/remount), logcat, adb shell, wipe data, open instance folder

## Requirements
- Windows 10/11 with **Windows Hypervisor Platform** enabled
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- Android SDK with `emulator`, `platform-tools`, and at least one AVD

## Setup
1. Install the Android SDK command-line tools, then:
   ```
   sdkmanager "emulator" "platform-tools" "system-images;android-35;google_apis_playstore;x86_64"
   avdmanager create avd -n Games -k "system-images;android-35;google_apis_playstore;x86_64" -d pixel_7
   ```
2. Download `CelestiumEmulator.zip` from the [latest release](../../releases/latest) and unzip it.
3. Edit `launcher.json` so `SdkRoot` points at your Android SDK folder.
4. Run `DroidLauncher.exe`.

## Build from source
```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```
