using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using static VirtualScreenViewer.Native;

namespace VirtualScreenViewer;

/// <summary>
/// Enables and disables the Virtual Display Driver's device, as in Device Manager. Needs admin
/// rights, so only the service (running as SYSTEM) calls this. The device is found by its hardware
/// ID every time, so a reinstall that gives it a new instance ID doesn't matter.
/// </summary>
internal static class DeviceControl
{
    public const string HardwareId = @"Root\MttVDD";
    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");

    public static bool IsInstalled()
    {
        if (!Find(out var set, out _)) return false;
        SetupDiDestroyDeviceInfoList(set);
        return true;
    }

    public static bool IsEnabled()
    {
        if (!Find(out var set, out var data)) return false;
        try { return CM_Get_DevNode_Status(out _, out int problem, data.DevInst, 0) == 0 && problem != CM_PROB_DISABLED; }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    public static void SetEnabled(bool enabled)
    {
        if (!Find(out var set, out var data)) throw new InvalidOperationException("The Virtual Display Driver isn't installed.");
        try
        {
            var p = new SP_PROPCHANGE_PARAMS
            {
                cbSize = 8, // the SP_CLASSINSTALL_HEADER part
                InstallFunction = DIF_PROPERTYCHANGE,
                StateChange = enabled ? DICS_ENABLE : DICS_DISABLE,
                Scope = DICS_FLAG_GLOBAL,
            };
            if (!SetupDiSetClassInstallParamsW(set, ref data, ref p, Marshal.SizeOf<SP_PROPCHANGE_PARAMS>())) throw new Win32Exception();
            if (!SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref data)) throw new Win32Exception();
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>The device's entry in a device list; the caller destroys <paramref name="set"/> when found.</summary>
    private static bool Find(out IntPtr set, out SP_DEVINFO_DATA data)
    {
        var cls = DisplayClass;
        set = SetupDiGetClassDevsW(ref cls, null, IntPtr.Zero, DIGCF_PRESENT); // disabled devices count as present
        if (set == new IntPtr(-1)) throw new Win32Exception();
        data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
        var buffer = new byte[4096];
        for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
        {
            Array.Clear(buffer);
            if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, SPDRP_HARDWAREID, out _, buffer, buffer.Length, out _)) continue;
            var ids = Encoding.Unicode.GetString(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (ids.Any(id => id.Equals(HardwareId, StringComparison.OrdinalIgnoreCase))) return true;
        }
        SetupDiDestroyDeviceInfoList(set);
        set = IntPtr.Zero;
        return false;
    }
}
