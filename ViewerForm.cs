using Microsoft.Win32;
using static VirtualScreenViewer.Native;

namespace VirtualScreenViewer;

/// <summary>
/// A window that shows the virtual display live, like a monitor in a window, with a tray icon and
/// global hotkeys. If the virtual display was off when the app started, the app brings it all the
/// way up (device enabled, monitor attached) and takes it all the way down again on exit, so it
/// costs nothing while the app isn't running.
/// </summary>
public sealed class ViewerForm : Form
{
    private readonly GpuView _view = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly NotifyIcon _tray = new();
    private ViewerSettings _settings;
    private Screen? _target;
    private string? _message = "Starting the virtual display…";
    private bool _busy = true;   // powering the device on/off in the background
    private bool _poweredOn;     // we enabled the device, so disable it again on exit
    private bool _attached;      // we attached the monitor, so detach it again on exit
    private bool _control;       // control mode: clicks in the window go to the virtual display
    private bool _exiting;
    private IntPtr _lastOtherWindow; // the last foreground window that wasn't ours (for "move window" from the tray)

    private readonly FileSystemWatcher _settingsWatcher;
    private readonly System.Windows.Forms.Timer _settingsDebounce = new() { Interval = 300 };
    private readonly System.Windows.Forms.Timer _fitDebounce = new() { Interval = 700 };
    private readonly System.Windows.Forms.Timer _foreground = new() { Interval = 400 };

    private const int HK_SHOW = 1, HK_MOVE = 2, HK_CONTROL = 3;

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

        _settings = ViewerSettings.Load(out var problem);
        if (problem is not null) Shown += (_, _) => ShowError($"{ViewerSettings.FilePath}:\n{problem}");
        RestoreBounds_();

        _view.CaptureLost += () => BeginInvoke(FindTarget);
        _view.MouseDown += OnViewMouseDown;
        _view.MouseWheel += OnViewMouseWheel;

        BuildTray();

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
        _foreground.Tick += (_, _) =>
        {
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != Handle && !IsOurs(fg)) _lastOtherWindow = fg;
        };
        _foreground.Start();

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

    /// <summary>The tray's "Virtual display on" item: the user's choice from now on, kept on exit.</summary>
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
        else if (_target is null)
            message = VirtualDisplay.FindDevice() is null
                ? "The virtual display is off.\n\nTurn it on from the tray icon."
                : "The virtual display is disconnected.\n\nTurn it on from the tray icon.";
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
        Invalidate();
    }

    private void SetMessage(string message) { _message = message; _view.Stop(); _view.Visible = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.Black);
        if (_message is not null)
            TextRenderer.DrawText(e.Graphics, _message, Font, ClientRectangle, Color.Gainsboro,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    protected override void OnMove(EventArgs e) { base.OnMove(e); if (IsHandleCreated && !_busy) UpdateCapture(); }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!IsHandleCreated) return;
        if (!_busy) UpdateCapture();
        if (_settings.FitToWindow && WindowState != FormWindowState.Minimized) { _fitDebounce.Stop(); _fitDebounce.Start(); }
    }

    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (IsHandleCreated && !_busy) UpdateCapture(); }

    // ---------- Fit the display's resolution to the window ----------

    private void FitResolution()
    {
        if (!_settings.FitToWindow || _target is null || _busy) return;
        var mode = VirtualDisplay.ModeFor(ClientSize, _target.Bounds.Size);
        if (mode is not { } m || (m.Width == _target.Bounds.Width && m.Height == _target.Bounds.Height)) return;
        try { VirtualDisplay.SetResolution(_target.DeviceName, m.Width, m.Height, _settings.RefreshRate); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    // ---------- Control mode: clicks and scrolling go to the virtual display ----------

    private void SetControl(bool on)
    {
        _control = on;
        _view.Highlight = on;
        _view.Cursor = on ? Cursors.Hand : Cursors.Default;
        Text = on ? "Virtual Screen — control mode (clicks go to the display)" : "Virtual Screen";
        _trayControl.Checked = on;
    }

    /// <summary>Where a point in the window lands on the virtual display, or null if it's in the black bars.</summary>
    private POINT? ToDisplay(Point p)
    {
        var rect = _view.PictureRect;
        if (_target is null || rect.Width < 1 || !rect.Contains(p)) return null;
        var b = _target.Bounds;
        return new POINT
        {
            X = b.X + (int)((p.X - rect.X) * (double)b.Width / rect.Width),
            Y = b.Y + (int)((p.Y - rect.Y) * (double)b.Height / rect.Height),
        };
    }

    private void OnViewMouseDown(object? s, MouseEventArgs e)
    {
        if (!_control || ToDisplay(e.Location) is not { } at) return;
        (uint down, uint up) = e.Button switch
        {
            MouseButtons.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            MouseButtons.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };
        _view.Capture = false; // let the real button-up go where the click went
        SendAt(at, Mouse(down), Mouse(up));
    }

    private void OnViewMouseWheel(object? s, MouseEventArgs e)
    {
        if (!_control || ToDisplay(e.Location) is not { } at) return;
        var wheel = Mouse(MOUSEEVENTF_WHEEL);
        wheel.mi.mouseData = e.Delta;
        SendAt(at, wheel);
    }

    private static INPUT Mouse(uint flags) => new() { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = flags } };

    /// <summary>Jumps the cursor to <paramref name="at"/>, sends the input there, and puts the cursor back.</summary>
    private static void SendAt(POINT at, params INPUT[] inputs)
    {
        GetCursorPos(out var back);
        SetCursorPos(at.X, at.Y);
        SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
        // SendInput only queues the input; give it a moment to be delivered at the new position.
        Thread.Sleep(15);
        SetCursorPos(back.X, back.Y);
    }

    // ---------- Move a window to or from the virtual display ----------

    private bool IsOurs(IntPtr hwnd) => hwnd == Handle || OwnedForms.Any(f => f.IsHandleCreated && f.Handle == hwnd);

    /// <summary>Moves a window onto the virtual display, or back to the screen this viewer is on.</summary>
    private void MoveWindow(IntPtr hwnd)
    {
        hwnd = hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, 2 /* GA_ROOT */);
        if (hwnd == IntPtr.Zero || IsOurs(hwnd) || !IsWindowVisible(hwnd) || _target is null) return;
        if (!GetWindowRect(hwnd, out var r)) return;
        var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        var from = Screen.FromRectangle(rect);
        var to = from.DeviceName == _target.DeviceName ? Screen.FromControl(this) : _target;
        if (to.DeviceName == from.DeviceName) return;

        bool maximized = IsZoomed(hwnd);
        if (maximized) { ShowWindow(hwnd, SW_RESTORE); GetWindowRect(hwnd, out r); rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom); }

        // Same place relative to the screen's working area, shrunk to fit if it's bigger.
        var a = from.WorkingArea; var bArea = to.WorkingArea;
        int w = Math.Min(rect.Width, bArea.Width), h = Math.Min(rect.Height, bArea.Height);
        double fx = a.Width > rect.Width ? (rect.X - a.X) / (double)(a.Width - rect.Width) : 0;
        double fy = a.Height > rect.Height ? (rect.Y - a.Y) / (double)(a.Height - rect.Height) : 0;
        int x = bArea.X + (int)(Math.Clamp(fx, 0, 1) * (bArea.Width - w));
        int y = bArea.Y + (int)(Math.Clamp(fy, 0, 1) * (bArea.Height - h));
        SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        if (maximized) ShowWindow(hwnd, SW_MAXIMIZE);
    }

    // ---------- Tray icon ----------

    private readonly ToolStripMenuItem _trayShow = new("Show viewer");
    private readonly ToolStripMenuItem _trayDisplay = new("Virtual display on");
    private readonly ToolStripMenuItem _trayControl = new("Control mode (clicks go to the display)");
    private readonly ToolStripMenuItem _trayFit = new("Fit resolution to window");
    private readonly ToolStripMenuItem _trayMove = new("Move active window to/from the display");

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        _trayShow.Font = new Font(_trayShow.Font, FontStyle.Bold);
        _trayShow.Click += (_, _) => ToggleShown();
        _trayDisplay.Click += (_, _) => ToggleDisplay();
        _trayControl.Click += (_, _) => SetControl(!_control);
        _trayFit.Click += (_, _) => { _settings.FitToWindow = !_settings.FitToWindow; _settings.Save(); if (_settings.FitToWindow) FitResolution(); };
        _trayMove.Click += (_, _) => MoveWindow(_lastOtherWindow);
        menu.Items.AddRange(new ToolStripItem[]
        {
            _trayShow, new ToolStripSeparator(), _trayDisplay, _trayControl, _trayFit, _trayMove, new ToolStripSeparator(),
            new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings()),
            new ToolStripMenuItem("Exit", null, (_, _) => Close()),
        });
        menu.Opening += (_, _) =>
        {
            var device = VirtualDisplay.FindDevice();
            _trayShow.Text = Visible ? "Hide viewer" : "Show viewer";
            _trayDisplay.Checked = device is not null && VirtualDisplay.IsOn(device);
            _trayDisplay.Enabled = !_busy;
            _trayFit.Checked = _settings.FitToWindow;
            _trayMove.Enabled = _target is not null;
            _trayShow.ShortcutKeyDisplayString = _settings.ShowHideHotkey;
            _trayControl.ShortcutKeyDisplayString = _settings.ControlHotkey;
            _trayMove.ShortcutKeyDisplayString = _settings.MoveWindowHotkey;
        };
        _tray.ContextMenuStrip = menu;
        _tray.Icon = SystemIcons.Application;
        _tray.Text = "Virtual Screen";
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleShown(); };
        _tray.Visible = true;
    }

    private void ToggleShown()
    {
        if (Visible && WindowState != FormWindowState.Minimized) { Hide(); return; }
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OpenSettings()
    {
        using var dlg = new SettingsForm(_settings);
        if (dlg.ShowDialog(Visible ? this : null) != DialogResult.OK) return;
        _settings.Save();          // the file watcher applies it
        RegisterHotkeys();
    }

    // ---------- Hotkeys ----------

    private void RegisterHotkeys()
    {
        if (!IsHandleCreated) return;
        var failed = new List<string>();
        foreach (var (id, text) in new[] { (HK_SHOW, _settings.ShowHideHotkey), (HK_MOVE, _settings.MoveWindowHotkey), (HK_CONTROL, _settings.ControlHotkey) })
        {
            UnregisterHotKey(Handle, id);
            if (ViewerSettings.ParseHotkey(text) is not { } hk) continue;
            if (!RegisterHotKey(Handle, id, hk.Mods | MOD_NOREPEAT, (uint)hk.Key)) failed.Add(text);
        }
        if (failed.Count > 0)
            _tray.ShowBalloonTip(5000, "Virtual Screen", $"Another app already uses {string.Join(", ", failed)}. Pick another in Settings.", ToolTipIcon.Warning);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            switch ((int)m.WParam)
            {
                case HK_SHOW: ToggleShown(); break;
                case HK_MOVE: MoveWindow(GetForegroundWindow()); break; // hotkeys leave the foreground window alone
                case HK_CONTROL: SetControl(!_control); break;
            }
            return;
        }
        base.WndProc(ref m);
    }

    // ---------- Settings ----------

    private void ReloadSettings()
    {
        var bounds = _settings.Window;
        _settings = ViewerSettings.Load(out var problem);
        _settings.Window ??= bounds;
        if (problem is not null) { ShowError($"{ViewerSettings.FilePath}:\n{problem}"); return; }
        RegisterHotkeys();
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
        RegisterHotkeys();
    }

    private void ShowError(string message) =>
        MessageBox.Show(Visible ? this : null, message, "Virtual display", MessageBoxButtons.OK, MessageBoxIcon.Warning);

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
        foreach (var id in new[] { HK_SHOW, HK_MOVE, HK_CONTROL }) UnregisterHotKey(Handle, id);
        _tray.Visible = false;
        _tray.Dispose();
        _settingsWatcher.Dispose();
        _settingsDebounce.Dispose();
        _fitDebounce.Dispose();
        _foreground.Dispose();
        _view.Stop();
        ShutDownDisplay();
        base.OnFormClosed(e);
    }
}
