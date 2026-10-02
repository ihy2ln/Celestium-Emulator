# Command line

`celestium.exe` ships next to the app. It lets scripts, build tools and AI agents manage devices, and it's useful when several projects need their own phone at the same time. You can name a device by its name or by its project label.

```
celestium list
celestium start Weaverse --wait            # prints emulator-5556 once it has booted
celestium start CI --wait --headless --memory 2048
celestium install Weaverse app-debug.apk   # starts it and waits if needed
celestium stop Weaverse
celestium new Weaverse --from Dev --project Weaverse
celestium project Dev "Adams Haven"
celestium memory Dev 3072                  # or: celestium memory Dev default
celestium serial Weaverse
celestium --version
```

| Command | What it does |
|---|---|
| list | Every device with its project, serial and state |
| start <device> | Boot it. `--wait` blocks until Android has booted, `--cold` skips the snapshot, `--headless` runs it without a window, `--memory <MB>` caps its RAM. |
| stop <device> | Shut it down |
| serial <device> | Print its fixed adb serial |
| install <device> <apk>… | Install APKs, starting the device first if needed |
| new <name> --from <device> | Create a device with the same Android version and settings |
| project <device> [label] | Set or clear the project label |
| memory <device> <MB or default> | Set its RAM for future starts |

## Using it with adb, Gradle and Unity

Most Android tools target the device named in `ANDROID_SERIAL`:

```
$env:ANDROID_SERIAL = celestium serial Weaverse
./gradlew installDebug
```

In Unity, pick the device in **Build Settings › Run Device**. Its serial is shown on the device's tile and on Overview.
