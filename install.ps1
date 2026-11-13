# Installs Virtual Screen: the Virtual Display Driver, the service that switches it on and off,
# and the app (in Program Files, with a Start menu shortcut). Needs admin; it will ask.
#   1. Download the driver into the VirtualDisplayDriver folder and run build.bat (see README).
#   2. Right-click this file -> Run with PowerShell. Run it again after rebuilding to update.
#   Add -Uninstall to remove everything again (your settings in %LOCALAPPDATA% are kept).
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$HardwareId = 'Root\MttVDD'
$DriverDir = Join-Path $PSScriptRoot 'VirtualDisplayDriver'
$BuiltExe = Join-Path $PSScriptRoot 'out\VirtualScreenViewer.exe'
$AppDir = Join-Path $env:ProgramFiles 'Barton IT\Virtual Screen'
$AppExe = Join-Path $AppDir 'VirtualScreenViewer.exe'
$ServiceName = 'BartonITVirtualScreen'
$Shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Virtual Screen.lnk'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Uninstall) { $argList += '-Uninstall' }
    Start-Process powershell -Verb RunAs -ArgumentList $argList
    exit
}

function Find-Device { Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Where-Object { $_.HardwareID -contains $HardwareId } }

function Stop-App {
    if (Get-Process VirtualScreenViewer -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ne 0 }) {
        throw 'Close Virtual Screen first, then run this again.'
    }
    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) { Stop-Service $ServiceName -Force }
}

# Earlier versions switched the device with two scheduled tasks; the service replaces them.
function Remove-OldTasks {
    $svc = New-Object -ComObject Schedule.Service
    $svc.Connect()
    try { $folder = $svc.GetFolder('\Barton IT') } catch { return }
    foreach ($name in 'Virtual display on', 'Virtual display off') { try { $folder.DeleteTask($name, 0) } catch {} }
    if ($folder.GetTasks(1).Count -eq 0) { try { $svc.GetFolder('\').DeleteFolder('Barton IT', 0) } catch {} }
}

function Get-DriverCertThumbprint {
    $cer = Join-Path $DriverDir 'Virtual_Display_Driver.cer'
    if (Test-Path $cer) { return (New-Object Security.Cryptography.X509Certificates.X509Certificate2 $cer).Thumbprint }
}

try {
    if ($Uninstall) {
        Stop-App
        if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
            Write-Host 'Removing the service...'
            sc.exe delete $ServiceName | Out-Null
        }
        Remove-OldTasks
        foreach ($d in @(Find-Device)) {
            Write-Host 'Removing the virtual display device...'
            pnputil /remove-device $d.InstanceId | Out-Host
        }
        if ($thumb = Get-DriverCertThumbprint) {
            Write-Host 'No longer trusting the driver certificate...'
            foreach ($store in 'Root', 'TrustedPublisher') {
                Get-ChildItem "Cert:\LocalMachine\$store\$thumb" -ErrorAction SilentlyContinue | Remove-Item
            }
        }
        else { Write-Host "Driver certificate not removed (Virtual_Display_Driver.cer isn't in $DriverDir to identify it)." -ForegroundColor Yellow }
        Remove-Item $Shortcut -ErrorAction SilentlyContinue
        Remove-Item $AppDir -Recurse -Force -ErrorAction SilentlyContinue
        $parent = Split-Path $AppDir
        if ((Test-Path $parent) -and -not (Get-ChildItem $parent)) { Remove-Item $parent }
        Write-Host 'Virtual Screen removed.' -ForegroundColor Green
    }
    else {
        if (-not (Test-Path $BuiltExe)) { throw "$BuiltExe not found. Run build.bat first." }
        $missing = 'MttVDD.inf', 'MttVDD.dll', 'mttvdd.cat', 'Virtual_Display_Driver.cer', 'vdd_settings.xml' |
            Where-Object { -not (Test-Path (Join-Path $DriverDir $_)) }
        if ($missing) { throw "Missing from ${DriverDir}: $($missing -join ', '). Download the driver first (see README)." }
        Stop-App

        # ---- Driver ----
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

        # ---- App ----
        Write-Host "Copying the app to $AppDir..."
        New-Item -ItemType Directory -Force $AppDir | Out-Null
        Get-ChildItem (Split-Path $BuiltExe) -File | Where-Object { $_.Extension -notin '.pdb', '.json' } |
            Copy-Item -Destination $AppDir -Force

        $shell = New-Object -ComObject WScript.Shell
        $lnk = $shell.CreateShortcut($Shortcut)
        $lnk.TargetPath = $AppExe
        $lnk.Description = 'Show the virtual display in a window'
        $lnk.Save()

        # ---- Service ----
        # Runs as SYSTEM and switches the device on only while the app is open. Local, logged-on
        # users can ask it to; nothing else can.
        if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
            Write-Host 'Adding the service...'
            New-Service -Name $ServiceName -BinaryPathName "`"$AppExe`" --service" -DisplayName 'Barton IT Virtual Screen' `
                -Description 'Switches the virtual display on while Virtual Screen is open, and off when it closes.' `
                -StartupType Automatic | Out-Null
            sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
        }
        Start-Service $ServiceName
        Remove-OldTasks

        Write-Host 'Virtual Screen installed. Open it from the Start menu.' -ForegroundColor Green
    }
}
catch {
    Write-Host "Failed: $($_.Exception.Message)" -ForegroundColor Red
}
Read-Host 'Press Enter to close'
