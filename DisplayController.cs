namespace VirtualScreenViewer;

internal enum DisplayState { Off, Starting, On, Stopping }

/// <summary>
/// Brings the virtual display up (device on via the service, then monitor attached) and down
/// again, one step at a time: a second request waits for the first to finish, so overlapping
/// clicks or closing mid-start can't leave it half on. Call from the UI thread; <see cref="Changed"/>
/// is raised there too.
/// </summary>
internal sealed class DisplayController : IDisposable
{
    private readonly PowerClient _power = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DisplayState State { get; private set; } = DisplayState.Off;
    /// <summary>Why the last step failed, if it did.</summary>
    public string? Problem { get; private set; }
    public event Action? Changed;

    public async Task TurnOnAsync(ViewerSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            if (State == DisplayState.On) return;
            Set(DisplayState.Starting, null);
            await _power.OnAsync();

            // Windows takes a moment to add the monitor after the device is enabled.
            string? device = null;
            for (int i = 0; i < 60 && (device = VirtualDisplay.FindDevice()) is null; i++) await Task.Delay(250);
            if (device is null) throw new InvalidOperationException("The virtual display was switched on but no monitor appeared.");

            string? problem = null;
            try
            {
                if (!VirtualDisplay.IsOn(device)) VirtualDisplay.Attach(device, settings);
                else VirtualDisplay.UseSettings(device, settings);
            }
            catch (Exception ex) { problem = ex.Message; } // on, just not at the mode asked for
            Set(DisplayState.On, problem);
        }
        catch (Exception ex) { Set(DisplayState.Off, ex.Message); }
        finally { _gate.Release(); }
    }

    public async Task TurnOffAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State == DisplayState.Off) return;
            Set(DisplayState.Stopping, null);
            DetachQuietly();
            string? problem = null;
            try { await _power.OffAsync(); } catch (Exception ex) { problem = ex.Message; }
            Set(DisplayState.Off, problem);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Applies changed settings to the display, if it's on. Throws if Windows refuses the mode.</summary>
    public void Apply(ViewerSettings settings)
    {
        if (State == DisplayState.On && VirtualDisplay.FindDevice() is { } device && VirtualDisplay.IsOn(device))
            VirtualDisplay.UseSettings(device, settings);
    }

    private void Set(DisplayState state, string? problem)
    {
        State = state;
        Problem = problem;
        Changed?.Invoke();
    }

    private static void DetachQuietly()
    {
        try { if (VirtualDisplay.FindDevice() is { } d && VirtualDisplay.IsOn(d)) VirtualDisplay.Detach(d); } catch { }
    }

    /// <summary>
    /// On exit: take the monitor off the desktop now, and drop the connection; the service then
    /// disables the device. Nothing here waits, so closing the window is instant.
    /// </summary>
    public void Dispose()
    {
        DetachQuietly();
        _power.Dispose();
    }
}
