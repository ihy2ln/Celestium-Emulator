# Windows and fleet tools

## Resizing device windows

Drag **any edge** of a device window and it resizes with the correct proportions. Celestium adds invisible grab strips along each edge, because the emulator itself only resizes from its corners. Dragging a corner still works too.

## Mouse and keyboard

Celestium is built for a mouse and keyboard. On a device window:

| You do | Android gets |
|---|---|
| Click | Tap |
| Click and drag | Swipe or fling |
| Hold the mouse button | Long press |
| Wheel | Smooth scroll (a fixed distance per notch, never a tap) |
| Shift+wheel, or tilting the wheel | Sideways scroll |
| Ctrl+wheel, or touchpad pinch | Pinch to zoom |
| Right-click, or the mouse's Back button | Back |
| Esc | Back |
| Typing, Enter, Backspace, arrow keys | The same keys, sent to the app |
| Ctrl+C / V / X / A / Z, Ctrl+arrows, Ctrl+Backspace | Normal text editing in the app |

Things to know:

- You can turn off the Back buttons (right-click, mouse Back and Esc) and the Ctrl shortcuts in **⋯ › Settings**.
- The Ctrl-shortcut setting applies to device windows opened afterwards.
- Esc only acts as Back while a device window is in front. Everywhere else it's untouched.
- To use the emulator's desktop-pointer mode for a device instead, turn on **Settings › Hardware › Mouse as a desktop pointer**.

## Fleet tools

These are in the **⋯** menu:

- **Start all devices** and **Stop all devices.**
- **Arrange device windows** tiles every device window in a grid on the monitor Celestium is on.
- **Always on top** (Overview) keeps a device window above other windows.

## Multiple Celestium windows

Press **⧉**, or open the exe while Celestium is already showing, to get another window. Only the first window owns the tray icon, reopens devices after a restart, and remembers its position. The others are plain windows that close normally.

## Settings

**⋯ › Settings**:

- **Theme:** follow Windows, light or dark. The **◐** button in the top bar cycles through them.
- **Start Celestium when Windows starts:** opens hidden in the tray.
- **Start hidden in the tray.**
- **Reopen devices that were running:** after a reboot, devices you left running start again.
- **Screenshots and recordings** folder.
- **Open data folder:** settings, key maps and macros live in `%APPDATA%\CelestiumEmulator`.

## Keyboard shortcuts

| Keys | Action |
|---|---|
| Ctrl+1 … Ctrl+6 | Switch tabs |
| Ctrl+N | New device |
| Ctrl+G | Game mode for the selected device |
| Ctrl+S | Screenshot of the selected device |
| F5 | Refresh |
| F1 | This help |
| Tab, Space, Enter | Move between and press buttons |
