# Installs the Virtual Display Driver from the VirtualDisplayDriver folder (needs admin; it will ask).
# The driver isn't part of this repo: download it first (see README).
#   Right-click -> Run with PowerShell.   Add -Uninstall to remove it again.
param([switch]$Uninstall, [string]$UserSid)

$ErrorActionPreference = 'Stop'
$HardwareId = 'Root\MttVDD'
$DriverDir = Join-Path $PSScriptRoot 'VirtualDisplayDriver'

# The account that runs the viewer may start the on/off tasks; remember it before elevating
# (the admin prompt may log in as a different account).
if (-not $UserSid) { $UserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-UserSid', $UserSid)
    if ($Uninstall) { $argList += '-Uninstall' }
    Start-Process powershell -Verb RunAs -ArgumentList $argList
    exit
}

$TaskFolder = 'Barton IT'
$Tasks = @{ 'Virtual display on' = '/enable-device'; 'Virtual display off' = '/disable-device' }

# Two tasks that enable/disable the device as SYSTEM. Their security descriptor lets this user run
# them (read + execute) but not change them, so the viewer can switch the device without a UAC prompt.
function Register-PowerTasks($instanceId) {
    $svc = New-Object -ComObject Schedule.Service
    $svc.Connect()
    $root = $svc.GetFolder('\')
    try { $folder = $root.GetFolder($TaskFolder) } catch { $folder = $root.CreateFolder($TaskFolder) }
    $sddl = "D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;$UserSid)"
    foreach ($name in $Tasks.Keys) {
        $def = $svc.NewTask(0)
        $def.RegistrationInfo.Description = 'Used by Virtual Screen Viewer to switch the virtual display device on and off.'
        $def.Settings.AllowDemandStart = $true
        $def.Settings.DisallowStartIfOnBatteries = $false
        $def.Settings.StopIfGoingOnBatteries = $false
        $def.Settings.ExecutionTimeLimit = 'PT2M'
        $def.Settings.MultipleInstances = 2   # ignore a second start while one runs
        $def.Principal.RunLevel = 1           # highest
        $action = $def.Actions.Create(0)
        $action.Path = "$env:SystemRoot\System32\pnputil.exe"
        $action.Arguments = "$($Tasks[$name]) `"$instanceId`""
        # 6 = create or update, 5 = service account
        $folder.RegisterTaskDefinition($name, $def, 6, 'SYSTEM', $null, 5, $sddl) | Out-Null
    }
}

function Remove-PowerTasks {
    $svc = New-Object -ComObject Schedule.Service
    $svc.Connect()
    try { $folder = $svc.GetFolder("\$TaskFolder") } catch { return }
    foreach ($name in $Tasks.Keys) { try { $folder.DeleteTask($name, 0) } catch {} }
    if ($folder.GetTasks(1).Count -eq 0) { try { $svc.GetFolder('\').DeleteFolder($TaskFolder, 0) } catch {} }
}

function Find-Device { Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Where-Object { $_.HardwareID -contains $HardwareId } }

try {
    if ($Uninstall) {
        $dev = Find-Device
        if (-not $dev) { Write-Host 'Virtual Display Driver is not installed.' }
        foreach ($d in $dev) { pnputil /remove-device $d.InstanceId | Out-Host }
        Remove-PowerTasks
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
        $dev = @(Find-Device)[0]
        if (-not $dev) { throw 'The device was not created.' }
        Write-Host 'Letting the viewer switch the device on and off (scheduled tasks under "Barton IT")...'
        Register-PowerTasks $dev.InstanceId
        Write-Host 'Virtual Display Driver installed. The viewer switches it on when it opens and off again when it closes.' -ForegroundColor Green
    }
}
catch {
    Write-Host "Failed: $($_.Exception.Message)" -ForegroundColor Red
}
Read-Host 'Press Enter to close'
