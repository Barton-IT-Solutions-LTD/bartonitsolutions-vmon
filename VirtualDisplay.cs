using System.Runtime.InteropServices;
using static VirtualScreenViewer.Native;

namespace VirtualScreenViewer;

/// <summary>
/// Turns the Virtual Display Driver's monitor on and off by attaching it to / detaching it from the
/// desktop (the same as "Disconnect this display" in Windows settings). No admin rights needed.
/// </summary>
internal static class VirtualDisplay
{
    private const string AdapterName = "Virtual Display Driver";

    /// <summary>The \\.\DISPLAYn name of the driver's monitor, whether it's attached or not.</summary>
    public static string? FindDevice()
    {
        // The adapter exposes many output slots; most have no monitor behind them.
        string? connected = null;
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref dd, 0); i++, dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>())
        {
            if (!dd.DeviceString.Contains(AdapterName,StringComparison.OrdinalIgnoreCase)) continue;
            if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0) return dd.DeviceName;
            var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (connected is null && EnumDisplayDevicesW(dd.DeviceName, 0, ref monitor, 0)) connected = dd.DeviceName;
        }
        return connected;
    }

    public static bool IsOn(string device)
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevicesW(null, i, ref dd, 0); i++, dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>())
            if (dd.DeviceName == device) return (dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
        return false;
    }

    public static void TurnOff(string device)
    {
        var dm = NewDevMode();
        dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT; // all zero = detach
        Apply(device, dm);
    }

    /// <summary>
    /// Attaches the monitor to the right of the existing screens, at the settings' resolution, else
    /// its saved one, else 1920×1080, and the settings' refresh rate.
    /// </summary>
    public static void TurnOn(string device, ViewerSettings settings)
    {
        var dm = NewDevMode();
        if (settings.Width > 0)
        {
            dm.dmPelsWidth = settings.Width;
            dm.dmPelsHeight = settings.Height;
        }
        else if (!EnumDisplaySettingsW(device, ENUM_REGISTRY_SETTINGS, ref dm) || dm.dmPelsWidth == 0 || dm.dmPelsHeight == 0)
        {
            dm = NewDevMode();
            dm.dmPelsWidth = 1920;
            dm.dmPelsHeight = 1080;
        }
        dm.dmPositionX = Screen.AllScreens.Max(s => s.Bounds.Right);
        dm.dmPositionY = Screen.PrimaryScreen?.Bounds.Top ?? 0;
        dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
        if (settings.RefreshRate > 0)
        {
            dm.dmDisplayFrequency = settings.RefreshRate;
            dm.dmFields |= DM_DISPLAYFREQUENCY;
        }
        Apply(device, dm);
    }

    /// <summary>Switches an attached monitor to the settings' resolution and refresh rate, where they differ.</summary>
    public static void UseSettings(string device, ViewerSettings settings)
    {
        var current = NewDevMode();
        if (!EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS, ref current)) return;
        var dm = NewDevMode();
        if (settings.Width > 0 && (current.dmPelsWidth != settings.Width || current.dmPelsHeight != settings.Height))
        {
            dm.dmPelsWidth = settings.Width;
            dm.dmPelsHeight = settings.Height;
            dm.dmFields |= DM_PELSWIDTH | DM_PELSHEIGHT;
        }
        if (settings.RefreshRate > 0 && current.dmDisplayFrequency != settings.RefreshRate)
        {
            dm.dmDisplayFrequency = settings.RefreshRate;
            dm.dmFields |= DM_DISPLAYFREQUENCY;
        }
        if (dm.dmFields != 0) Apply(device, dm);
    }

    private static DEVMODE NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    private static void Apply(string device, DEVMODE dm)
    {
        int r = ChangeDisplaySettingsExW(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        if (r == DISP_CHANGE_SUCCESSFUL) r = ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        if (r == DISP_CHANGE_BADMODE)
            throw new InvalidOperationException("The virtual display doesn't offer that resolution and refresh rate. "
                + @"Pick one listed in C:\VirtualDisplayDriver\vdd_settings.xml.");
        if (r != DISP_CHANGE_SUCCESSFUL) throw new InvalidOperationException($"Windows refused the display change (code {r}).");
    }
}
