# Getting started

Celestium Emulator is a Windows app for running Android phones and tablets on your PC. It runs on Google's official Android Emulator.

## What you need

- Windows 10 or 11 with **Windows Hypervisor Platform** turned on (Settings › Apps › Optional features › More Windows features).
- The **.NET 9 Desktop Runtime**.
- The **Android SDK** with the `emulator` and `platform-tools` packages and at least one virtual device (AVD).

## Install

1. Download `CelestiumEmulator.zip` from the latest release on GitHub and unzip it to a folder you'll keep, for example `S:\AI\Emulator\CelestiumEmulator`.
2. Open `launcher.json` and set `SdkRoot` to your Android SDK folder.
3. Run `CelestiumEmulator.exe`.

Keep the folder in the same place. Updates are installed into it, and antivirus exclusions apply to it.

## The main window

- **Sidebar:** one tile per device, with a live screenshot, its status and its specs. Click a tile to select it. Double-click it to start the device, or to bring its window to the front.
- **Detail panel:** tabs for the selected device: **Overview**, **Controls**, **Apps**, **Files**, **Snapshots** and **Settings**.
- **Top bar:**
  - **+ New device** creates a device.
  - **Install APK…** installs an app.
  - **⧉** opens another window.
  - **◐** switches the theme.
  - **⋯** opens the fleet tools, Settings and Help.

## Starting your first device

Select a device and press **Start**. The first boot takes about a minute. After that, devices resume from a quick-boot snapshot in a few seconds.

## The tray

The **X** button hides Celestium to the system tray instead of quitting, and your devices keep running. Click the tray icon to bring the window back, or right-click it to:

- start or stop a device
- take a screenshot or record the screen
- open game mode
- quit

Opening the exe again also brings back the existing window.
