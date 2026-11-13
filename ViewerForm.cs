using Microsoft.Win32;
using static VirtualScreenViewer.Native;

namespace VirtualScreenViewer;

/// <summary>
/// A window that shows the virtual display live, like a monitor in a window, with a small settings
/// menu (Ctrl+Alt+S, or the title bar's right-click menu). If the virtual display was off when the
/// app started, the app brings it all the way up (device enabled, monitor attached) and takes it all
/// the way down again on exit, so it costs nothing while the app isn't running.
/// </summary>
public sealed class ViewerForm : Form
{
    private readonly GpuView _view = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly MenuPanel _menu = new();
    private ViewerSettings _settings;
    private Screen? _target;
    private string? _message = "Starting the virtual display…";
    private bool _busy = true;   // powering the device on/off in the background
    private bool _poweredOn;     // we enabled the device, so disable it again on exit
    private bool _attached;      // we attached the monitor, so detach it again on exit
    private bool _exiting;

    private readonly FileSystemWatcher _settingsWatcher;
    private readonly System.Windows.Forms.Timer _settingsDebounce = new() { Interval = 300 };
    private readonly System.Windows.Forms.Timer _fitDebounce = new() { Interval = 700 };

    private const int HK_MENU = 1;
    private const uint ID_MENU = 0x10; // system menu command IDs keep the low four bits clear

    public ViewerForm()
    {
        Text = "Virtual Screen";
        Icon = SystemIcons.Application;
        ClientSize = new Size(1280, 720);
        MinimumSize = new Size(320, 200);
        BackColor = Color.Black;
        ForeColor = Color.Gainsboro;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Controls.Add(_view);
        Controls.Add(_menu); // docked right; the picture fills the rest
        _menu.DisplayToggled += ToggleDisplay;
        _menu.CloseClicked += ToggleMenu;
        _menu.SettingsChanged += () => { _settings.Save(); RegisterHotkey(); }; // the file watcher applies the display part
        _view.Resize += (_, _) =>
        {
            if (_settings.FitToWindow && WindowState != FormWindowState.Minimized) { _fitDebounce.Stop(); _fitDebounce.Start(); }
        };
        _view.CaptureLost += () => BeginInvoke(FindTarget);

        _settings = ViewerSettings.Load(out var problem);
        if (problem is not null) Shown += (_, _) => ShowError($"{ViewerSettings.FilePath}:\n{problem}");
        RestoreBounds_();

        // Editors save in bursts (truncate, write, rename), so wait for the file to settle.
        _settingsWatcher = new FileSystemWatcher(AppContext.BaseDirectory, Path.GetFileName(ViewerSettings.FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler changed = (_, _) =>
        {
            if (IsHandleCreated) BeginInvoke(() => { _settingsDebounce.Stop(); _settingsDebounce.Start(); });
        };
        _settingsWatcher.Changed += changed;
        _settingsWatcher.Created += changed;
        _settingsWatcher.Renamed += (s, e) => changed(s, e);
        _settingsDebounce.Tick += (_, _) => { _settingsDebounce.Stop(); ReloadSettings(); };
        _fitDebounce.Tick += (_, _) => { _fitDebounce.Stop(); FitResolution(); };

        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Shown += (_, _) => _ = StartDisplayAsync();
    }

    // ---------- Bringing the display up and down ----------

    /// <summary>On start: enable the device if needed (background), then attach the monitor.</summary>
    private async Task StartDisplayAsync()
    {
        _busy = true;
        string? device = VirtualDisplay.FindDevice();
        if (device is null)
        {
            if (!await Task.Run(() => VirtualDisplay.CanPowerOff))
            {
                _busy = false;
                SetMessage("The virtual display is switched off in Device Manager.\n\n"
                    + "Run install-driver.ps1 again so this app can switch it on and off itself.");
                return;
            }
            device = await Task.Run(VirtualDisplay.PowerOn);
            _poweredOn = device is not null;
        }
        _busy = false;
        if (device is null)
        {
            SetMessage("No virtual display found.\n\nInstall the Virtual Display Driver (see README).");
            return;
        }
        try
        {
            if (!VirtualDisplay.IsOn(device)) { VirtualDisplay.TurnOn(device, _settings); _attached = true; }
            else VirtualDisplay.UseSettings(device, _settings);
        }
        catch (Exception ex) { ShowError(ex.Message); }
        FindTarget();
    }

    /// <summary>The menu's "Virtual display on" box: the user's choice from now on, kept on exit.</summary>
    private async void ToggleDisplay()
    {
        if (_busy) return;
        _poweredOn = _attached = false;
        var device = VirtualDisplay.FindDevice();
        try
        {
            if (device is not null && VirtualDisplay.IsOn(device))
            {
                VirtualDisplay.TurnOff(device);
                _busy = true;
                SetMessage("Switching the virtual display off…");
                if (await Task.Run(() => VirtualDisplay.CanPowerOff)) await Task.Run(VirtualDisplay.PowerOff);
            }
            else
            {
                _busy = true;
                SetMessage("Switching the virtual display on…");
                device ??= await Task.Run(VirtualDisplay.PowerOn);
                if (device is null) ShowError("Couldn't switch the virtual display on. Run install-driver.ps1 again.");
                else VirtualDisplay.TurnOn(device, _settings);
            }
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _busy = false; FindTarget(); }
    }

    private void ShutDownDisplay()
    {
        var device = VirtualDisplay.FindDevice();
        if (device is null) return;
        if (_attached || _poweredOn) try { VirtualDisplay.TurnOff(device); } catch { }
        if (_poweredOn || _attached && VirtualDisplay.CanPowerOff) VirtualDisplay.PowerOff();
    }

    // ---------- Which display to show ----------

    private void OnDisplaysChanged(object? s, EventArgs e) { if (IsHandleCreated) BeginInvoke(FindTarget); }

    private void FindTarget()
    {
        if (_exiting) return;
        var device = VirtualDisplay.FindDevice();
        _target = device is null ? null : Screen.AllScreens.FirstOrDefault(s => s.DeviceName == device);
        UpdateCapture();
    }

    private bool ShowingOwnScreen => _target is not null && _target.DeviceName == Screen.FromControl(this).DeviceName;

    /// <summary>Capture when there's something to show and somewhere to show it; otherwise a message.</summary>
    private void UpdateCapture()
    {
        string? message = null;
        if (_busy) message = _message;
        else if (!GpuView.Supported) message = "Screen capture isn't available on this version of Windows (needs Windows 10 2004 or later).";
        else if (_target is null) message = "The virtual display is off.\n\nTurn it on in the menu (Ctrl+Alt+S).";
        else if (ShowingOwnScreen) message = "This window is on the display it's showing.\nMove it to another screen.";

        bool capture = message is null && Visible && WindowState != FormWindowState.Minimized;
        if (capture)
        {
            var center = new POINT { X = _target!.Bounds.X + _target.Bounds.Width / 2, Y = _target.Bounds.Y + _target.Bounds.Height / 2 };
            var monitor = MonitorFromPoint(center, MONITOR_DEFAULTTONULL);
            if (!_view.IsCapturing || _view.Tag is not IntPtr m || m != monitor)
            {
                try { _view.Start(monitor); _view.Tag = monitor; }
                catch (Exception ex) { message = $"Couldn't capture the virtual display: {ex.Message}"; capture = false; }
            }
        }
        if (!capture) _view.Stop();
        _view.Visible = capture;
        _message = message;
        SyncMenu();
        Invalidate();
    }

    private void SetMessage(string message) { _message = message; _view.Stop(); _view.Visible = false; SyncMenu(); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.Black);
        if (_message is not null)
            TextRenderer.DrawText(e.Graphics, _message, Font, _view.Bounds, Color.Gainsboro,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    protected override void OnMove(EventArgs e) { base.OnMove(e); if (IsHandleCreated && !_busy) UpdateCapture(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (IsHandleCreated && !_busy) UpdateCapture(); }

    // ---------- Fit the display's resolution to the window ----------

    private void FitResolution()
    {
        if (!_settings.FitToWindow || _target is null || _busy) return;
        var mode = VirtualDisplay.ModeFor(_view.ClientSize, _target.Bounds.Size);
        if (mode is not { } m || (m.Width == _target.Bounds.Width && m.Height == _target.Bounds.Height)) return;
        try { VirtualDisplay.SetResolution(_target.DeviceName, m.Width, m.Height, _settings.RefreshRate); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    // ---------- Settings menu ----------

    private void SyncMenu()
    {
        if (_menu.Visible) _menu.Sync(_settings, VirtualDisplay.FindDevice(), _busy);
    }

    /// <summary>Opens or closes the settings menu at the side of the viewer.</summary>
    private void ToggleMenu()
    {
        if (WindowState == FormWindowState.Minimized) { WindowState = FormWindowState.Normal; _menu.Visible = false; }
        _menu.Visible = !_menu.Visible;
        SyncMenu();
        if (_menu.Visible) Activate();
    }

    /// <summary>Puts the viewer in front (someone launched the app again).</summary>
    internal void BringToFront_()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void RegisterHotkey()
    {
        if (!IsHandleCreated) return;
        UnregisterHotKey(Handle, HK_MENU);
        // If another app already has the shortcut, the title bar's right-click menu still opens it.
        if (ViewerSettings.ParseHotkey(_settings.MenuHotkey) is { } hk)
            RegisterHotKey(Handle, HK_MENU, hk.Mods | MOD_NOREPEAT, (uint)hk.Key);
    }

    protected override void WndProc(ref Message m)
    {
        if ((m.Msg == WM_HOTKEY && (int)m.WParam == HK_MENU)
            || (m.Msg == WM_SYSCOMMAND && ((uint)m.WParam & 0xFFF0) == ID_MENU))
        {
            ToggleMenu();
            return;
        }
        base.WndProc(ref m);
    }

    // ---------- Settings file ----------

    private void ReloadSettings()
    {
        var bounds = _settings.Window;
        _settings = ViewerSettings.Load(out var problem);
        _settings.Window ??= bounds;
        if (problem is not null) { ShowError($"{ViewerSettings.FilePath}:\n{problem}"); return; }
        RegisterHotkey();
        SyncMenu();
        if (VirtualDisplay.FindDevice() is not { } device || !VirtualDisplay.IsOn(device)) return;
        try { VirtualDisplay.UseSettings(device, _settings); }
        catch (Exception ex) { ShowError(ex.Message); }
        if (_settings.FitToWindow) FitResolution();
    }

    private void RestoreBounds_()
    {
        if (_settings.Window is not { Length: >= 4 } w) return;
        var rect = new Rectangle(w[0], w[1], Math.Max(w[2], MinimumSize.Width), Math.Max(w[3], MinimumSize.Height));
        // Only if the title bar would be on a screen (not the virtual one, which may not exist yet).
        var titleBar = new Rectangle(rect.X + 40, rect.Y + 8, Math.Max(1, rect.Width - 80), 1);
        if (!Screen.AllScreens.Any(s => s.DeviceName != VirtualDisplay.FindDevice() && s.WorkingArea.IntersectsWith(titleBar))) return;
        StartPosition = FormStartPosition.Manual;
        Bounds = rect;
        if (w.Length > 4 && w[4] == 1) WindowState = FormWindowState.Maximized;
    }

    private void SaveBounds()
    {
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.Window = new[] { b.X, b.Y, b.Width, b.Height, WindowState == FormWindowState.Maximized ? 1 : 0 };
        _settingsWatcher.EnableRaisingEvents = false;
        _settings.Save();
    }

    // ---------- Theme and errors ----------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTheme();
        RegisterHotkey();
        var sys = GetSystemMenu(Handle, false);
        AppendMenuW(sys, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(sys, MF_STRING, ID_MENU, "Settings menu");
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "Virtual display", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void OnUserPreferenceChanged(object? s, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && IsHandleCreated) BeginInvoke(ApplyTheme);
    }

    private void ApplyTheme()
    {
        int dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is 0 ? 1 : 0;
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    // ---------- Exit ----------

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _exiting = true;
        SaveBounds();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        UnregisterHotKey(Handle, HK_MENU);
        _settingsWatcher.Dispose();
        _settingsDebounce.Dispose();
        _fitDebounce.Dispose();
        _view.Stop();
        ShutDownDisplay();
        base.OnFormClosed(e);
    }
}
