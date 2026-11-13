# Virtual Screen Viewer

Shows a virtual "second monitor" live inside a window on your real monitor. Move apps onto it, and capture the virtual screen in OBS or Sunshine to stream it.

The virtual monitor comes from the [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) by MikeTheTech and the VirtualDrivers contributors. It isn't included in this repo; you download it yourself (step 1).

## 1. Install the virtual display driver (one time)

1. Download a release from https://github.com/VirtualDrivers/Virtual-Display-Driver/releases. This app was tested with the October 2024 driver (version 24.10.27).
2. Put these files from the download in a `VirtualDisplayDriver` folder next to this README: `MttVDD.inf`, `MttVDD.dll`, `mttvdd.cat`, `Virtual_Display_Driver.cer` and `vdd_settings.xml`.
3. Right-click `install-driver.ps1` → **Run with PowerShell** and accept the admin prompt. It trusts the driver's certificate, copies `vdd_settings.xml` to `C:\VirtualDisplayDriver\`, creates the virtual display device, and adds two scheduled tasks (Task Scheduler → **Barton IT** → *Virtual display on* / *off*). The tasks enable and disable the device as SYSTEM; only your account may start them, so the viewer can switch the device without an admin prompt. Run it with `-Uninstall` to remove the device and the tasks.

Already installed with an older version? Run `install-driver.ps1` again to add the tasks.

`C:\VirtualDisplayDriver\vdd_settings.xml` lists the resolutions and refresh rates the driver offers. Restart the driver or reboot after editing it.

## 2. Build the app

Install the **.NET 8 SDK** (https://dotnet.microsoft.com/download/dotnet/8.0), then double-click `build.bat`.
The finished app is `out\VirtualScreenViewer.exe`. It needs Windows 10 2004 or later.

## 3. Use it

Run `out\VirtualScreenViewer.exe`. It's a window showing the virtual display live, like a monitor in a window. Resize it however you like and the picture scales to fit. Move apps onto the virtual display with Win + Shift + arrow keys, or by dragging them past the edge of your screen.

- **Fully off when you're not using it.** If the virtual display is off when the app opens, the app enables the device and connects the display; on exit it disconnects it and disables the device again, as if you'd disabled it in Device Manager. Windows then sees no extra monitor and no extra adapter.
- **Menu**: press `Ctrl+Alt+S`, or right-click the title bar → **Settings menu**. A small panel opens at the side of the viewer where you can:
  - switch the virtual display on or off (once you do, the app leaves it as you set it when it exits);
  - pick its resolution and refresh rate (only the ones the driver offers);
  - turn on **Fit resolution to window**: when you resize the viewer, the display switches to the driver's resolution closest to the window's shape, so there are no black bars;
  - open Windows' display settings, or change the menu's shortcut.

  Changes apply straight away. Press the shortcut again or ✕ to close it.
- Only one viewer runs at a time; opening it again brings the running one to the front.
- The window remembers its size and position, and its title bar follows Windows' light/dark app setting.
- Capture uses Windows Graphics Capture on the graphics card (as OBS does), at the display's full frame rate, with the cursor included.

## Settings

The menu sets everything. Settings are kept in `VirtualScreenViewer.json` next to the exe (`out\`), which you can also edit by hand; changes apply as soon as it's saved.

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

Only the modes listed in `C:\VirtualDisplayDriver\vdd_settings.xml` work; anything else shows an error and leaves the display as it was. A mistake in the file shows a message and the defaults are used until it's fixed.

Higher refresh rates cost more compositor time. 60 Hz suits OBS at 60 fps; use more only if you stream it at a higher frame rate (for example 120 Hz to Moonlight).

## Streaming

- **OBS**: add a *Display Capture* source and select the virtual monitor. You stream it while still using your real monitor freely.
- **Sunshine + Moonlight**: in Sunshine, set the output display to the virtual monitor.

## Known limits

- The viewer is for watching only: use the virtual display through its apps, OBS or Moonlight.
- Some fullscreen-exclusive games or DRM video show black. Run games in borderless windowed mode.
- On Windows 10 the virtual display gets a yellow "being captured" border (Windows 11 lets the app turn it off).
- If the window is on the display it's showing, it'll tell you to move it, to avoid a hall-of-mirrors effect.
