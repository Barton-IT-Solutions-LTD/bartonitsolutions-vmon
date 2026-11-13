using System.Runtime.InteropServices;
using static VirtualScreenViewer.Native;

namespace VirtualScreenViewer;

/// <summary>
/// The Virtual Display Driver's monitor. Two levels of "off":
/// <list type="bullet">
/// <item>Power: the device itself enabled or disabled, as in Device Manager. Disabled, Windows sees
/// no monitor at all. Needs admin, so it goes through two scheduled tasks that install-driver.ps1
/// creates and only this user may start (no UAC prompt).</item>
/// <item>Attach: the monitor on or off the desktop ("Disconnect this display"). No admin needed.</item>
/// </list>
/// </summary>
internal static class VirtualDisplay
{
    private const string AdapterName = "Virtual Display Driver";
    private const string PowerOnTask = @"\Barton IT\Virtual display on";
    private const string PowerOffTask = @"\Barton IT\Virtual display off";
    public const string DriverSettingsFile = @"C:\VirtualDisplayDriver\vdd_settings.xml";

    // ---------- Power (the device itself) ----------

    /// <summary>Whether install-driver.ps1 set up the power tasks (older installs didn't).</summary>
    public static bool CanPowerOff => RunSchtasks($"/query /tn \"{PowerOffTask}\"");

    /// <summary>Enables the device and waits for its monitor to appear. Returns it, or null.</summary>
    public static string? PowerOn()
    {
        if (FindDevice() is { } existing) return existing;
        if (!RunSchtasks($"/run /tn \"{PowerOnTask}\"")) return null;
        for (int i = 0; i < 60; i++) // up to 15 seconds
        {
            Thread.Sleep(250);
            if (FindDevice() is { } device) return device;
        }
        return null;
    }

    /// <summary>Disables the device, so Windows no longer sees a monitor (detach it first).</summary>
    public static bool PowerOff() => RunSchtasks($"/run /tn \"{PowerOffTask}\"");

    private static bool RunSchtasks(string args)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // ---------- Modes the driver offers ----------

    /// <summary>The resolutions in the driver's settings file, each with its refresh rates.</summary>
    public static List<(int Width, int Height, int[] Rates)> Modes()
    {
        var modes = new List<(int, int, int[])>();
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(DriverSettingsFile);
            foreach (var r in doc.Descendants("resolution"))
            {
                if (!int.TryParse((string?)r.Element("width"), out int w) || !int.TryParse((string?)r.Element("height"), out int h)) continue;
                var rates = r.Elements("refresh_rate").Select(e => int.TryParse(e.Value, out int hz) ? hz : 0).Where(hz => hz > 0).ToArray();
                modes.Add((w, h, rates));
            }
        }
        catch { }
        return modes;
    }

    /// <summary>
    /// The driver's resolution closest in shape to <paramref name="box"/>; among equally close ones,
    /// the one nearest the current size (so only the shape changes, not how big things look).
    /// </summary>
    public static (int Width, int Height)? ModeFor(Size box, Size current)
    {
        if (box.Width < 1 || box.Height < 1) return null;
        double want = (double)box.Width / box.Height;
        double area = Math.Max(1, (double)current.Width * current.Height);
        return Modes()
            .Select(m => (m.Width, m.Height, Shape: Math.Abs(Math.Log((double)m.Width / m.Height / want)),
                Size: Math.Abs(Math.Log(m.Width * (double)m.Height / area))))
            .OrderBy(m => Math.Round(m.Shape, 2)).ThenBy(m => m.Size)
            .Select(m => ((int, int)?)(m.Width, m.Height))
            .FirstOrDefault();
    }

    /// <summary>Switches an attached monitor to this resolution, keeping its refresh rate if the settings don't set one.</summary>
    public static void SetResolution(string device, int width, int height, int refreshRate)
    {
        var dm = NewDevMode();
        dm.dmPelsWidth = width;
        dm.dmPelsHeight = height;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
        if (refreshRate > 0) { dm.dmDisplayFrequency = refreshRate; dm.dmFields |= DM_DISPLAYFREQUENCY; }
        Apply(device, dm);
    }

    /// <summary>The attached monitor's current resolution and refresh rate.</summary>
    public static (int Width, int Height, int Hz)? Current(string device)
    {
        var dm = NewDevMode();
        return EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS, ref dm) ? (dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency) : null;
    }

    // ---------- Attach (on or off the desktop) ----------

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
