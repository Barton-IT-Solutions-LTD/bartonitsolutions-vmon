# Virtual Screen Viewer

Shows a virtual "second monitor" live inside a window on your real monitor. Move apps onto it, and capture the virtual screen in OBS or Sunshine to stream it.

The virtual monitor comes from the [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) by MikeTheTech and the VirtualDrivers contributors. It isn't included in this repo; you download it yourself (step 1).

## 1. Install the virtual display driver (one time)

1. Download a release from https://github.com/VirtualDrivers/Virtual-Display-Driver/releases. This app was tested with the October 2024 driver (version 24.10.27).
2. Put these files from the download in a `VirtualDisplayDriver` folder next to this README: `MttVDD.inf`, `MttVDD.dll`, `mttvdd.cat`, `Virtual_Display_Driver.cer` and `vdd_settings.xml`.
3. Right-click `install-driver.ps1` → **Run with PowerShell** and accept the admin prompt. It trusts the driver's certificate, copies `vdd_settings.xml` to `C:\VirtualDisplayDriver\`, and creates the virtual display device. Run it with `-Uninstall` to remove the device again.

`C:\VirtualDisplayDriver\vdd_settings.xml` lists the resolutions and refresh rates the driver offers. Restart the driver or reboot after editing it.

## 2. Build the app

Install the **.NET 8 SDK** (https://dotnet.microsoft.com/download/dotnet/8.0), then double-click `build.bat`.
The finished app is `out\VirtualScreenViewer.exe`.

## 3. Use it

Run `out\VirtualScreenViewer.exe`. It's a plain window showing the virtual display live, like a monitor in a window: resize it however you like and the picture scales to fit.

- If the virtual display is off when the window opens, it's turned on, and turned off again when you close the window.
- Right-click the title bar → **Virtual display connected** to connect or disconnect it any time. A disconnected display costs nothing, so there's no need to disable the adapter.
- The app runs the virtual display at 60 Hz. The driver offers up to 165 Hz, but that costs much more compositor time.
- The title bar follows Windows' light/dark app setting.
- Move apps onto the virtual display with Win + Shift + arrow keys, or by dragging them past the right edge of your main screen.

## Streaming

- **OBS**: add a *Display Capture* source and select the virtual monitor. You stream it while still using your real monitor freely.
- **Sunshine + Moonlight**: in Sunshine, set the output display to the virtual monitor.

## Known limits

- The window is view-only: clicks and typing in it don't reach the virtual display.
- Capture uses GDI (~30–60 fps). Some fullscreen-exclusive games or DRM video show black. Run games in borderless windowed mode.
- If the window is on the display it's showing, it'll tell you to move it, to avoid a hall-of-mirrors effect.
