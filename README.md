# Celestium Emulator

A lightweight Windows launcher for the official Android Emulator — a replacement for MuMu Player. Run multiple Android instances, install APKs by drag-and-drop, and open logcat/adb shell in one click.

## Features
- One card per Android Virtual Device (AVD) with **Start / Stop** and live status (stopped → booting → running)
- **Install APK…** button, or drag `.apk` files onto the window
- **More ▾**: cold boot, writable-system boot (root/remount), logcat, adb shell, wipe data, open instance folder
- **Closes to the system tray**: the X button hides the window and the app keeps running. Click the tray icon to reopen it, or right-click it to start or stop instances or **Quit**. Launching the exe again brings back the existing window.
- **Many devices at once**: **+ New** creates an instance with the same settings as an existing one. Each instance has a fixed adb serial (`emulator-<port>`) and an optional **project label**, so several projects can each have their own device without clashing.
- **Memory control**: under More ▾ → Memory, cap each device's RAM (2–8 GB). **Run in background (no window)** saves more RAM and GPU for devices that tools drive. A 2 GB background device uses about 1.5 GB instead of about 5 GB.
- **Multiple windows**: the ⧉ button, or opening the exe while it's already visible, opens another window.
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
4. Run `CelestiumEmulator.exe`. Keep the folder in the same place, so updates and antivirus exclusions keep applying.

## Command line (for scripts, build tools and agents)
`celestium.exe` ships next to the app. Instances can be addressed by name or project label:
```
celestium list
celestium new Weaverse --from Dev --project Weaverse
celestium start Weaverse --wait            # prints emulator-5556 once it has booted
celestium start CI --wait --headless --memory 2048
celestium install Weaverse app-debug.apk   # starts it and waits if needed
celestium stop Weaverse
```
Most Android tools target the device in `ANDROID_SERIAL`:
```powershell
$env:ANDROID_SERIAL = celestium serial Weaverse; ./gradlew installDebug
```

## Updating
Run `powershell -ExecutionPolicy Bypass -File update.ps1` from the source folder. It builds the app and updates the installed copy in place (default `S:\AI\Emulator\CelestiumEmulator`). It keeps your `launcher.json` and won't overwrite the app while it's running.

## Antivirus warnings (Norton and others)
The exe isn't code-signed yet, so reputation-based scanners like Norton CyberCapture treat it as an unknown file and may check it the first time it runs. To make that a one-time check, **the .exe files are byte-identical in every release**: the .NET SDK is pinned in `global.json`, and the exe's icon, manifest and version stamp are frozen. Updates only change the `.dll` files, so an exe you've already approved stays approved. The app requests no admin rights. To stop the warnings:
- **Norton:** go to Settings → Antivirus → Scans and Risks → *Items to Exclude from Scans* and *Items to Exclude from Auto-Protect*, and add the folder you unzipped to.
- **Report a false positive:** submit the file at https://submit.norton.com so Norton whitelists it for everyone.

## Build from source
```
dotnet publish CelestiumEmulator.csproj -c Release -r win-x64 --self-contained false -o dist
dotnet publish cli/celestium.csproj -c Release -r win-x64 --self-contained false -o dist
```
