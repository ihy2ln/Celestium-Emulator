# Devices

## Creating a device

Press **+ New device** (Ctrl+N) and choose:

- **Name:** letters, numbers, `.`, `_` or `-`.
- **Android version from:** the new device copies this device's Android version and system image. It starts empty, so apps aren't copied.
- **Device profile:**

| Profile | Screen |
|---|---|
| Phone | 6.2", 1080 × 2340, 420 dpi |
| Phone Plus | 6.7", 1080 × 2400, 400 dpi |
| Phone Ultra | 6.8" QHD+, 1440 × 3120, 500 dpi |
| Compact | 5.8", 1080 × 2280, 440 dpi |
| Foldable | 7.6" inner screen, 1768 × 2208 (has fold and unfold controls) |
| Tablet | 11", 1600 × 2560, 320 dpi |

- **Performance:**

| Preset | RAM | CPU cores | Good for |
|---|---|---|---|
| Low | 2 GB | 2 | background test devices |
| Balanced | 3 GB | 4 | everyday apps |
| High | 4 GB | 6 | heavier apps and most games |
| Ultra | 6 GB | 8 | demanding 3D games |

- **Project label:** optional, e.g. "Weaverse". It's shown on the tile, and tools can target the device by it.

Each device gets a **fixed adb serial** (`emulator-5554`, `emulator-5556`, …) that never changes. That way tools working on several projects at once always reach the right phone.

## Changing a device

Open **Settings** on a device to change its:

- **Device profile:** only while the device is stopped. The next start is a cold boot.
- **Memory and CPU,** or pick a preset on **Overview**. Changes apply the next time the device starts.
- **Run in background (no window):** no screen and less RAM and GPU, for devices that only tools use.
- **Mouse as a desktop pointer:** off by default, so your mouse works like a finger:
  - **click** = tap
  - **drag** = swipe or fling
  - **wheel** = smooth scroll

  Turn it on only if you want the emulator's desktop-mouse mode, with a pointer and hover. Many apps won't scroll or respond to clicks in that mode. The change applies the next time the device starts.
- **Camera and microphone:** each camera can be a virtual scene, your PC webcam, or off. The PC microphone is off unless you turn it on.
- **Project label.**

## Managing devices

These are in **Settings › Manage**:

- **Duplicate…** makes a new device with the same settings.
- **Cold boot** starts without the quick-boot snapshot. Use it if a device misbehaves.
- **Writable system** starts with a writable system partition, for root and remount work on non-Play images.
- **Wipe data…** erases all apps and data. This can't be undone.
- **Delete device…** removes the device and everything on it. This can't be undone.

## Snapshots

The **Snapshots** tab saves a device's exact state, such as "fresh install" or "level 10", and restores it in seconds. "Quick boot (automatic)" is the snapshot the emulator saves on shutdown.

## Memory limits

Celestium won't let devices take over your PC:

- **Device limit:** all running devices together may use at most **35% of your RAM** by default (about 22 GB on a 64 GB PC). Each device counts as its RAM setting plus about 1.5 GB for the emulator itself. You can change the limit in **⋯ › Settings** to 25, 35, 50 or 75%, or no limit.
- **Free-memory check:** a device only starts if Windows has room for it plus 2 GB to spare.
- **When a start would go over the limit:** Celestium explains why and offers to start the device with less RAM. If even the minimum won't fit, it tells you what to stop. Devices reopened after a reboot follow the same rules.
- **Every device starts with an explicit RAM size,** so it can never use more than its setting.

**⋯ › Free up memory** shows what's using RAM and can:

- **stop idle Android build daemons:** Gradle and Kotlin servers that stay in memory for hours after a build, often 5–8 GB
- **unload ComfyUI's AI models,** if it's running
- **stop devices**

## Memory and disk

A running device uses about as much RAM as its memory setting. A 2 GB device in background mode uses about 1.5 GB, and a 4 GB device with a window about 5 GB. Each device also takes several GB of disk after its first boot, mostly the quick-boot snapshot.
