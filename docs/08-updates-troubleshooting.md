# Updates and troubleshooting

## Updates

Celestium checks GitHub for a new version twice a day. When there is one, an **⬆ Update** button appears in the top bar. Press it to see what's new. Celestium then installs the update in place and restarts, and your devices keep running. You can also check from **⋯ › Check for updates** or from the tray.

## Antivirus (Norton and others)

Celestium isn't code-signed, so antivirus that checks reputation may look at it the first time it runs. To keep that a one-time check, every release ships the **same `.exe` files byte for byte**, and updates only change the `.dll` files. To stop the checks completely, add the Celestium folder to your antivirus exclusions. In Norton: Settings › Antivirus › Scans and Risks › *Items to Exclude from Scans* and *Items to Exclude from Auto-Protect*.

## Troubleshooting

**A device says "didn't start".** Try **Settings › Cold boot**. If that fails, check that Windows Hypervisor Platform is on, and that you have free disk space on the drive where devices are stored.

**Clicks don't open apps, dragging doesn't scroll, or the wheel acts like a click.** The device is using the emulator's desktop-mouse mode. In **Settings › Hardware**, make sure **Mouse as a desktop pointer** is off, press **Save hardware**, and restart the device. With it off, clicks are taps, drags are swipes, and the wheel scrolls smoothly.

**Ctrl+C, Ctrl+V or Ctrl+arrow keys rotate the phone or do something else.** Turn on **⋯ › Settings › Ctrl shortcuts go to apps**, then restart the device so its window picks up the change.

**The screen preview is black.** The device is still booting, or its screen is off. Press **Power** on the Controls tab.

**Game mode doesn't react to keys.** Click the phone screen once so game mode has keyboard focus. Controller input only works while the game is in front.

**Key hints are in the wrong place.** The key map was made in the other orientation. Rotate the window to match, or press **✎ Edit** and drag the controls.

**`adb devices` shows `emulator-5562 offline`.** That's usually another emulator, such as MuMu, or a stale entry. Celestium ignores it.

**Where are my settings?** In `%APPDATA%\CelestiumEmulator`: `state.json` (window and app settings), `instances.json` (ports, labels, memory) and `keymaps\` (key maps and macros). Devices themselves live in `%USERPROFILE%\.android\avd`.

## Getting help

Report problems and request features at https://github.com/ihy2ln/Celestium-Emulator/issues
