# Celestium Emulator

A lightweight Windows launcher for the official Android Emulator — a replacement for MuMu Player. Run multiple Android instances, install APKs by drag-and-drop, and open logcat/adb shell in one click.

## Features
- One card per Android Virtual Device (AVD) with **Start / Stop** and live status (stopped → booting → running)
- **Install APK…** button, or drag `.apk` files onto the window
- **More ▾**: cold boot, writable-system boot (root/remount), logcat, adb shell, wipe data, open instance folder
- **Closes to the system tray**: the X button hides the window and the app keeps running. Click the tray icon to reopen it, or right-click it to start or stop instances or **Quit**. Launching the exe again brings back the existing window.
- **Remembers its state**: window size and position, your last APK folder, and which instances were running. Those instances reopen on the next launch, even after a reboot. You can turn this off from the tray menu. The state is saved in `%APPDATA%\CelestiumEmulator\state.json`.

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
4. Run `CelestiumEmulator.exe`.

## Antivirus warnings (Norton and others)
The exe isn't code-signed yet, so reputation-based scanners like Norton Insight treat it as an unknown file and may scan or block it. It's built from the source in this repo, and the build has version info and a manifest that requests no admin rights. To stop the warnings:
- **Norton:** go to Settings → Antivirus → Scans and Risks → *Items to Exclude from Scans* and *Items to Exclude from Auto-Protect*, and add the folder you unzipped to.
- **Report a false positive:** submit the file at https://submit.norton.com so Norton whitelists it for everyone.

## Build from source
```
dotnet publish -c Release -r win-x64 --self-contained false -o dist
```
