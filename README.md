# Celestium Emulator

A lightweight Windows launcher for the official Android Emulator — a replacement for MuMu Player. Run multiple Android instances, install APKs by drag-and-drop, and open logcat/adb shell in one click.

## Features
- **Modern design:** a device sidebar with live screen thumbnails, a detail panel with Overview, Controls, Snapshots and Settings tabs, and light and dark themes that follow Windows. Shortcuts: Ctrl+1–4 switch tabs, Ctrl+N creates a device, Ctrl+S takes a screenshot, F5 refreshes.
- **Resize from any edge:** drag any side of an emulator window and it scales with the correct proportions. Normally the emulator only resizes from its corners.
- **Device profiles:** Phone, Phone Plus, Phone Ultra (QHD+), Compact, Foldable (with fold/unfold) and Tablet.
- **Performance presets:** Low, Balanced, High and Ultra set RAM and CPU cores. Live CPU and RAM usage for each device.
- **Phone controls:** Back, Home, Recents, Power, Volume, Rotate, GPS location (city presets or coordinates), battery level and charging, mobile data speed, Wi-Fi, airplane mode, dark theme, gesture navigation, animation speed, shake, and fingerprint touch.
- **Screenshots and screen recording:** one click, from the app or the tray. Saved to Pictures\Celestium.
- **Snapshots:** save a device's exact state and restore it in seconds.
- **Camera and microphone:** each camera can be a virtual scene, your PC webcam, or off, and you can use your PC microphone.
- **Files:** drag APKs to install them, or drag any other file to send it to the phone's Downloads folder.
- **Many devices at once,** each with a project label and a fixed adb serial. Multiple app windows.
- **Background mode** (no window) and memory caps for devices that tools drive.
- **Closes to the system tray** and remembers its state, including which devices were running.

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
