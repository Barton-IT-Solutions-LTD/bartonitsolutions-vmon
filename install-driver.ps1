# Installs the Virtual Display Driver from the VirtualDisplayDriver folder (needs admin; it will ask).
# The driver isn't part of this repo: download it first (see README).
#   Right-click -> Run with PowerShell.   Add -Uninstall to remove it again.
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$HardwareId = 'Root\MttVDD'
$DriverDir = Join-Path $PSScriptRoot 'VirtualDisplayDriver'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Uninstall) { $argList += '-Uninstall' }
    Start-Process powershell -Verb RunAs -ArgumentList $argList
    exit
}

function Find-Device { Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Where-Object { $_.HardwareID -contains $HardwareId } }

try {
    if ($Uninstall) {
        $dev = Find-Device
        if (-not $dev) { Write-Host 'Virtual Display Driver is not installed.' }
        foreach ($d in $dev) { pnputil /remove-device $d.InstanceId | Out-Host }
        Write-Host 'Removed.' -ForegroundColor Green
    }
    else {
        $missing = 'MttVDD.inf', 'MttVDD.dll', 'mttvdd.cat', 'Virtual_Display_Driver.cer', 'vdd_settings.xml' |
            Where-Object { -not (Test-Path (Join-Path $DriverDir $_)) }
        if ($missing) { throw "Missing from ${DriverDir}: $($missing -join ', '). Download the driver first (see README)." }

        $inf = Join-Path $DriverDir 'MttVDD.inf'

        Write-Host 'Trusting the driver certificate...'
        $cert = Join-Path $DriverDir 'Virtual_Display_Driver.cer'
        Import-Certificate -FilePath $cert -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
        Import-Certificate -FilePath $cert -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null

        # The driver reads its monitor count and resolutions from here.
        New-Item -ItemType Directory -Force 'C:\VirtualDisplayDriver' | Out-Null
        Copy-Item (Join-Path $DriverDir 'vdd_settings.xml') 'C:\VirtualDisplayDriver\vdd_settings.xml' -Force

        if (Find-Device) {
            Write-Host 'Device already exists, updating its driver...'
            pnputil /add-driver $inf /install | Out-Host
        }
        else {
            Write-Host 'Creating the virtual display device...'
            # Same as "devcon install MttVDD.inf Root\MttVDD": a root-enumerated device, then the driver on it.
            Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class RootDevice {
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid cls, IntPtr hwnd);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string name, ref Guid cls, string desc, IntPtr hwnd, int flags, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, int prop, byte[] buf, int size);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiCallClassInstaller(int fn, IntPtr set, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwnd, string hwid, string inf, int flags, out bool reboot);

    public static bool Install(string hwid, string inf) {
        var cls = new Guid("4d36e968-e325-11ce-bfc1-08002be10318"); // Display
        IntPtr set = SetupDiCreateDeviceInfoList(ref cls, IntPtr.Zero);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        try {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
            if (!SetupDiCreateDeviceInfoW(set, "Display", ref cls, null, IntPtr.Zero, 1 /* DICD_GENERATE_ID */, ref data)) throw new Win32Exception();
            byte[] ids = System.Text.Encoding.Unicode.GetBytes(hwid + "\0\0");
            if (!SetupDiSetDeviceRegistryPropertyW(set, ref data, 1 /* SPDRP_HARDWAREID */, ids, ids.Length)) throw new Win32Exception();
            if (!SetupDiCallClassInstaller(0x19 /* DIF_REGISTERDEVICE */, set, ref data)) throw new Win32Exception();
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        bool reboot;
        if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hwid, inf, 1 /* INSTALLFLAG_FORCE */, out reboot)) throw new Win32Exception();
        return reboot;
    }
}
'@
            if ([RootDevice]::Install($HardwareId, $inf)) { Write-Host 'Windows says a restart is needed to finish.' -ForegroundColor Yellow }
        }
        Write-Host 'Virtual Display Driver installed. Use "Virtual display: on/off" in the viewer.' -ForegroundColor Green
    }
}
catch {
    Write-Host "Failed: $($_.Exception.Message)" -ForegroundColor Red
}
Read-Host 'Press Enter to close'
