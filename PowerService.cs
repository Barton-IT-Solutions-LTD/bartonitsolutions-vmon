using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Timer = System.Threading.Timer;

namespace VirtualScreenViewer;

/// <summary>
/// The Windows service (the same exe, started with --service) that switches the virtual display's
/// device on and off for the viewer, which runs without admin rights.
///
/// The viewer connects to a local named pipe and sends "ON" or "OFF". The device stays enabled
/// only while some viewer holds it on: when the viewer sends "OFF", exits, crashes or is killed,
/// its connection drops and the device is disabled a moment later. So the virtual display can't be
/// left on by accident, and it's also switched off when the service starts (e.g. after a reboot).
/// </summary>
internal sealed class PowerService : ServiceBase
{
    public const string Name = "BartonITVirtualScreen";
    public const string PipeName = "BartonIT.VirtualScreen";
    private static readonly TimeSpan OffDelay = TimeSpan.FromSeconds(2); // a viewer restarting keeps it on

    private readonly object _lock = new();
    private CancellationTokenSource _stop = new();
    private int _holders;
    private Timer? _offTimer;

    public PowerService()
    {
        ServiceName = Name;
        CanStop = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        _stop = new CancellationTokenSource();
        TryDisable();
        _ = Task.Run(() => AcceptLoop(_stop.Token));
    }

    protected override void OnStop() => Shutdown();
    protected override void OnShutdown() => Shutdown();

    private void Shutdown()
    {
        _stop.Cancel();
        lock (_lock) { _offTimer?.Dispose(); _offTimer = null; _holders = 0; }
        TryDisable();
    }

    // ---------- Pipe ----------

    /// <summary>Only local, logged-on users may connect (and SYSTEM and admins); never the network.</summary>
    private static PipeSecurity Security()
    {
        var s = new PipeSecurity();
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return s;
    }

    private async Task AcceptLoop(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, Security());
            }
            catch (Exception ex) { Log($"Couldn't open the pipe: {ex.Message}"); await Task.Delay(1000, stop).ConfigureAwait(false); continue; }
            try { await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false); }
            catch { pipe.Dispose(); break; }
            _ = Task.Run(() => Serve(pipe, stop));
        }
    }

    private async Task Serve(NamedPipeServerStream pipe, CancellationToken stop)
    {
        bool holding = false;
        try
        {
            using var reader = new StreamReader(pipe);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            while (await reader.ReadLineAsync(stop).ConfigureAwait(false) is { } line)
            {
                string reply;
                try
                {
                    switch (line.Trim().ToUpperInvariant())
                    {
                        case "ON":
                            if (!holding) { Hold(); holding = true; } else EnsureEnabled();
                            reply = "OK";
                            break;
                        case "OFF":
                            if (holding) { Release(); holding = false; }
                            reply = "OK";
                            break;
                        case "STATUS":
                            reply = DeviceControl.IsEnabled() ? "ON" : "OFF";
                            break;
                        default:
                            reply = "ERR Unknown command.";
                            break;
                    }
                }
                catch (Exception ex) { reply = "ERR " + ex.Message.ReplaceLineEndings(" "); }
                await writer.WriteLineAsync(reply).ConfigureAwait(false);
            }
        }
        catch { } // the viewer went away
        finally
        {
            if (holding) Release();
            pipe.Dispose();
        }
    }

    // ---------- Holding the device on ----------

    private void Hold()
    {
        lock (_lock)
        {
            _offTimer?.Dispose(); _offTimer = null;
            if (!DeviceControl.IsEnabled()) DeviceControl.SetEnabled(true); // throws before counting, if it fails
            _holders++;
        }
    }

    private void EnsureEnabled()
    {
        lock (_lock) { if (!DeviceControl.IsEnabled()) DeviceControl.SetEnabled(true); }
    }

    private void Release()
    {
        lock (_lock)
        {
            if (_holders > 0) _holders--;
            if (_holders > 0) return;
            _offTimer?.Dispose();
            _offTimer = new Timer(_ => { lock (_lock) { if (_holders == 0) TryDisable(); } }, null, OffDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void TryDisable()
    {
        try { if (DeviceControl.IsEnabled()) DeviceControl.SetEnabled(false); }
        catch (Exception ex) { Log($"Couldn't switch the virtual display off: {ex.Message}"); }
    }

    private void Log(string message)
    {
        try { EventLog.WriteEntry(message, EventLogEntryType.Warning); } catch { }
    }
}
