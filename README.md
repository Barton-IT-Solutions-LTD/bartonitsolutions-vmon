# Virtual Screen

Shows a virtual "second monitor" live inside a window on your real monitor. Move apps onto it, and capture the virtual screen in OBS or Sunshine to stream it. The virtual display exists only while the app is open.

The virtual monitor comes from the [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) by MikeTheTech and the VirtualDrivers contributors. It isn't included in this repo; you download it yourself.

## Install

1. **Get the driver.** Download a release from https://github.com/VirtualDrivers/Virtual-Display-Driver/releases (tested with the October 2024 driver, version 24.10.27). Put these files from it in a `VirtualDisplayDriver` folder next to this README: `MttVDD.inf`, `MttVDD.dll`, `mttvdd.cat`, `Virtual_Display_Driver.cer` and `vdd_settings.xml`.
2. **Build the app.** Install the **.NET 8 SDK** (https://dotnet.microsoft.com/download/dotnet/8.0), then double-click `build.bat`. It makes `out\VirtualScreenViewer.exe`. Each push to GitHub also builds it (Actions → Build → the `VirtualScreenViewer` artifact).
3. **Install.** Right-click `install.ps1` → **Run with PowerShell** and accept the admin prompt. It:
   - trusts the driver's certificate and creates the virtual display device;
   - copies `vdd_settings.xml` to `C:\VirtualDisplayDriver\` (the driver's list of resolutions and refresh rates);
   - copies the app to `C:\Program Files\Barton IT\Virtual Screen\` and adds **Virtual Screen** to the Start menu;
   - adds the **Barton IT Virtual Screen** service, which switches the virtual display on and off for the app (see below).

To update after rebuilding, close the app and run `install.ps1` again. To remove everything (service, device, certificate, app), run it with `-Uninstall`. Your settings are kept.

It needs Windows 10 2004 or later.

## Use it

Open **Virtual Screen** from the Start menu. It's a window showing the virtual display live, like a monitor in a window. Resize it however you like and the picture scales to fit. Move apps onto the virtual display with Win + Shift + arrow keys, or by dragging them past the edge of your screen.

- **Only on while the app is open.** Opening the app switches the virtual display on. Closing it switches it fully off again, as if you'd disabled it in Device Manager, so Windows sees no extra monitor or adapter. That also happens if the app crashes or is ended in Task Manager.
- **Menu**: press `Ctrl+Alt+S`, or right-click the title bar → **Settings menu**. A small panel opens at the side of the viewer where you can:
  - switch the virtual display on or off while the app is open;
  - pick its resolution and refresh rate (only the ones the driver offers);
  - turn on **Fit resolution to window**: when you resize the viewer, the display switches to the driver's resolution closest to the window's shape, so there are no black bars;
  - open Windows' display settings, or change the menu's shortcut.

  Changes apply straight away. Press the shortcut again or ✕ to close it.
- Only one viewer runs at a time; opening it again brings the running one to the front.
- The window remembers its size and position, and follows Windows' light/dark setting.

## How the switching works

Enabling and disabling a device needs admin rights, and the app runs without them. So `install.ps1` adds a small service (the same exe, run with `--service`) that runs as SYSTEM:

- The app connects to the service over a local named pipe and asks it to switch the device on. Only local, logged-on users can connect; the network can't.
- The device stays on only while the app holds that connection. When the app asks for it off, closes, crashes or is killed, the connection ends and the service disables the device two seconds later.
- When the service starts (for example after a reboot), it makes sure the device is off.
- If something goes wrong, the service logs a warning in Event Viewer → Windows Logs → Application.

## Settings

The menu sets everything. Settings are kept in `%LOCALAPPDATA%\Barton IT\Virtual Screen\settings.json`, which you can also edit by hand; changes apply as soon as it's saved.

```json
{
  "refreshRate": 60,
  "width": 0,
  "height": 0,
  "fitToWindow": false,
  "menuHotkey": "Ctrl+Alt+S"
}
```

- `refreshRate`: Hz for the virtual display, e.g. 30, 60, 90, 120, 144 or 165. `0` leaves it as Windows has it.
- `width` / `height`: its resolution, e.g. `2560` and `1440`. `0` for both keeps the one set in Windows' display settings.
- `menuHotkey`: needs at least one of Ctrl, Alt, Shift or Win. Empty means none (the title bar menu still works).

Only the modes listed in `C:\VirtualDisplayDriver\vdd_settings.xml` work; anything else shows an error and leaves the display as it was. To offer more, edit that file and run `install.ps1` again (or reboot).

Higher refresh rates cost more compositor time. 60 Hz suits OBS at 60 fps; use more only if you stream it at a higher frame rate (for example 120 Hz to Moonlight).

## Streaming

- **OBS**: add a *Display Capture* source and select the virtual monitor. You stream it while still using your real monitor freely.
- **Sunshine + Moonlight**: in Sunshine, set the output display to the virtual monitor. The app must be open for the display to exist.

## Known limits

- The viewer is for watching. Use the virtual display through the apps on it, OBS or Moonlight.
- Some fullscreen-exclusive games or DRM video show black. Run games in borderless windowed mode.
- On Windows 10 the virtual display gets a yellow "being captured" border (Windows 11 lets the app turn it off).
- If the window is on the display it's showing, it'll tell you to move it, to avoid a hall-of-mirrors effect.
- If the graphics driver resets, the picture comes back by itself; after three failures in 30 seconds it shows the error instead, until the displays change.

## Project layout

| File | What it does |
|---|---|
| `Program.cs` | Starts the viewer, or the service with `--service`; one viewer at a time. |
| `ViewerForm.cs` | The window: picture, messages, menu, shortcut, window position. |
| `DisplayController.cs` | Switches the display on and off in order (off, starting, on, stopping). |
| `PowerService.cs` / `PowerClient.cs` | The service and the app's connection to it. |
| `DeviceControl.cs` | Enables and disables the device (SetupAPI), found by its hardware ID `Root\MttVDD`. |
| `VirtualDisplay.cs` | Finds the virtual monitor; attaches, detaches and sets its mode. |
| `GpuView.cs` | Live capture (Windows Graphics Capture) drawn with Direct2D. |
| `MenuPanel.cs` / `ViewerSettings.cs` | The settings menu and the settings file. |
| `install.ps1` | Install, update and uninstall. |
