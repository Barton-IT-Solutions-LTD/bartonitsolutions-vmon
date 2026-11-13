using Microsoft.Win32;
using static VirtualScreenViewer.Native;

namespace VirtualScreenViewer;

/// <summary>
/// A window that shows the virtual display live, like a monitor in a window, with a small settings
/// menu (Ctrl+Alt+S, or the title bar's right-click menu). Opening it switches the virtual display
/// on; closing it (or the app ending any other way) switches it fully off again.
/// </summary>
public sealed class ViewerForm : Form
{
    private readonly GpuView _view = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly MenuPanel _menu = new();
    private readonly DisplayController _display = new();
    private ViewerSettings _settings;
    private Screen? _target;
    private string? _message;
    private bool _exiting;

    private readonly FileSystemWatcher? _settingsWatcher;
    private readonly System.Windows.Forms.Timer _settingsDebounce = new() { Interval = 300 };
    private readonly System.Windows.Forms.Timer _fitDebounce = new() { Interval = 700 };

    // Capture recovery: restart after a failure (e.g. graphics driver reset), but not endlessly.
    private readonly Queue<DateTime> _failures = new();
    private string? _captureBroken;

    private const int HK_MENU = 1;
    private const uint ID_MENU = 0x10; // system menu command IDs keep the low four bits clear

    public ViewerForm()
    {
        // Sizes below are at 96 dpi; Windows Forms scales them for the screen, and again when the
        // window moves to a screen with a different scale.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Virtual Screen";
        Icon = SystemIcons.Application;
        ClientSize = new Size(1280, 720);
        MinimumSize = new Size(320, 200);
        BackColor = Color.Black;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Controls.Add(_view);
        Controls.Add(_menu); // docked right; the picture fills the rest

        _settings = ViewerSettings.Load(out var problem);
        if (problem is not null) Shown += (_, _) => ShowError($"{ViewerSettings.FilePath}:\n{problem}");

        _display.Changed += OnDisplayChanged;
        _menu.DisplayToggled += ToggleDisplay;
        _menu.CloseClicked += ToggleMenu;
        _menu.SettingsChanged += () => { _settings.Save(); ApplySettings(); };
        _view.Resize += (_, _) => { if (_settings.FitToWindow) { _fitDebounce.Stop(); _fitDebounce.Start(); } };
        _view.CaptureLost += () => BeginInvoke(FindTarget);
        _view.CaptureFailed += ex => BeginInvoke(() => OnCaptureFailed(ex));
        _fitDebounce.Tick += (_, _) => { _fitDebounce.Stop(); FitResolution(); };

        // Hand edits to the settings file apply when saved. Editors save in bursts, so wait for it to settle.
        try
        {
            _settingsWatcher = new FileSystemWatcher(Path.GetDirectoryName(ViewerSettings.FilePath)!, Path.GetFileName(ViewerSettings.FilePath))
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
        }
        catch { } // no folder: the menu still works
        _settingsDebounce.Tick += (_, _) => { _settingsDebounce.Stop(); ReloadSettings(); };

        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Shown += (_, _) => _ = _display.TurnOnAsync(_settings);
    }

    // ---------- The display ----------

    private void ToggleDisplay()
    {
        if (_display.State == DisplayState.On) _ = _display.TurnOffAsync();
        else if (_display.State == DisplayState.Off) _ = _display.TurnOnAsync(_settings);
    }

    private void OnDisplayChanged()
    {
        if (_exiting) return;
        if (_display.State == DisplayState.On && _display.Problem is { } problem) ShowError(problem);
        else if (_display.State == DisplayState.On && _settings.FitToWindow) FitResolution();
        FindTarget();
    }

    private void OnDisplaysChanged(object? s, EventArgs e) { if (IsHandleCreated) BeginInvoke(FindTarget); }

    private void FindTarget()
    {
        if (_exiting) return;
        _captureBroken = null; // the displays changed: worth trying again
        var device = _display.State == DisplayState.On ? VirtualDisplay.FindDevice() : null;
        _target = device is null ? null : Screen.AllScreens.FirstOrDefault(s => s.DeviceName == device);
        UpdateCapture();
    }

    private bool ShowingOwnScreen => _target is not null && _target.DeviceName == Screen.FromControl(this).DeviceName;

    // ---------- Capture ----------

    /// <summary>Capture when there's something to show and somewhere to show it; otherwise a message.</summary>
    private void UpdateCapture()
    {
        string? message = _display.State switch
        {
            DisplayState.Starting => "Switching the virtual display on…",
            DisplayState.Stopping => "Switching the virtual display off…",
            DisplayState.Off => _display.Problem is { } p
                ? $"The virtual display is off.\n\n{p}"
                : "The virtual display is off.\n\nTurn it on in the menu (Ctrl+Alt+S).",
            _ => null,
        };
        if (message is null)
        {
            if (!GpuView.Supported) message = "Screen capture isn't available on this version of Windows (needs Windows 10 2004 or later).";
            else if (_target is null) message = "Waiting for the virtual display…";
            else if (ShowingOwnScreen) message = "This window is on the display it's showing.\nMove it to another screen.";
            else if (_captureBroken is not null) message = $"Couldn't show the virtual display: {_captureBroken}";
        }

        bool capture = message is null && WindowState != FormWindowState.Minimized;
        if (capture)
        {
            var b = _target!.Bounds;
            var monitor = MonitorFromPoint(new POINT { X = b.X + b.Width / 2, Y = b.Y + b.Height / 2 }, MONITOR_DEFAULTTONULL);
            if (!_view.IsCapturing || _view.Monitor != monitor)
            {
                try { _view.Start(monitor); }
                catch (Exception ex) { message = $"Couldn't show the virtual display: {ex.Message}"; capture = false; }
            }
        }
        if (!capture) _view.Stop();
        _view.Visible = capture;
        _message = message;
        SyncMenu();
        Invalidate();
    }

    private void OnCaptureFailed(Exception ex)
    {
        if (_exiting) return;
        var now = DateTime.UtcNow;
        _failures.Enqueue(now);
        while (_failures.Peek() < now.AddSeconds(-30)) _failures.Dequeue();
        if (_failures.Count >= 3) _captureBroken = ex.Message; // stop retrying until the displays change
        UpdateCapture(); // starts again with a fresh graphics device
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.Black);
        // The picture's area (a hidden control isn't laid out, so its own bounds can't be used).
        var area = ClientRectangle;
        if (_menu.Visible) area.Width -= _menu.Width;
        if (_message is not null)
            TextRenderer.DrawText(e.Graphics, _message, Font, area, Color.Gainsboro,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }

    protected override void OnMove(EventArgs e) { base.OnMove(e); if (IsHandleCreated) UpdateCapture(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (IsHandleCreated) UpdateCapture(); }

    // ---------- Fit the display's resolution to the window ----------

    private void FitResolution()
    {
        if (!_settings.FitToWindow || _target is null || _display.State != DisplayState.On) return;
        var mode = VirtualDisplay.ModeFor(_view.ClientSize, _target.Bounds.Size);
        if (mode is not { } m || (m.Width == _target.Bounds.Width && m.Height == _target.Bounds.Height)) return;
        try { VirtualDisplay.SetResolution(_target.DeviceName, m.Width, m.Height, _settings.RefreshRate); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    // ---------- Settings ----------

    /// <summary>Applies the current settings: the menu shortcut, and the display's mode if it's on.</summary>
    private void ApplySettings()
    {
        RegisterHotkey();
        try { _display.Apply(_settings); }
        catch (Exception ex) { ShowError(ex.Message); }
        if (_settings.FitToWindow) FitResolution();
        SyncMenu();
    }

    /// <summary>The settings file changed: apply it, unless it's just our own save.</summary>
    private void ReloadSettings()
    {
        if (!ViewerSettings.ChangedElsewhere()) return;
        var window = _settings.Window;
        var loaded = ViewerSettings.Load(out var problem);
        if (problem is not null) { ShowError($"{ViewerSettings.FilePath}:\n{problem}"); return; }
        loaded.Window ??= window;
        _settings = loaded;
        ApplySettings();
    }

    private void SyncMenu()
    {
        if (_menu.Visible) _menu.Sync(_settings, _display.State, _display.Problem);
    }

    /// <summary>Opens or closes the settings menu at the side of the viewer.</summary>
    private void ToggleMenu()
    {
        if (WindowState == FormWindowState.Minimized) { WindowState = FormWindowState.Normal; _menu.Visible = false; }
        _menu.Visible = !_menu.Visible;
        SyncMenu();
        Invalidate(); // the message area changed size
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
        // If another app already has the shortcut, the title bar's right-click menu still opens the menu.
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

    // ---------- Window position ----------

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e); // DPI scaling happens here, so restore the saved (already scaled) bounds after it
        if (_settings.Window is not { Length: >= 4 } w) return;
        var rect = new Rectangle(w[0], w[1], Math.Max(w[2], MinimumSize.Width), Math.Max(w[3], MinimumSize.Height));
        // Only if the title bar would be on a real screen (not the virtual one, which isn't on yet).
        var titleBar = new Rectangle(rect.X + 40, rect.Y + 8, Math.Max(1, rect.Width - 80), 1);
        var virtualDevice = VirtualDisplay.FindDevice();
        if (!Screen.AllScreens.Any(s => s.DeviceName != virtualDevice && s.WorkingArea.IntersectsWith(titleBar))) return;
        Bounds = rect;
        if (w.Length > 4 && w[4] == 1) WindowState = FormWindowState.Maximized;
    }

    private void SaveBounds()
    {
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.Window = new[] { b.X, b.Y, b.Width, b.Height, WindowState == FormWindowState.Maximized ? 1 : 0 };
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
        MessageBox.Show(this, message, "Virtual Screen", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void OnUserPreferenceChanged(object? s, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && IsHandleCreated) BeginInvoke(ApplyTheme);
    }

    private void ApplyTheme()
    {
        bool dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is 0;
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        _menu.ApplyTheme(dark);
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
        _settingsWatcher?.Dispose();
        _settingsDebounce.Dispose();
        _fitDebounce.Dispose();
        _view.Stop();
        _display.Dispose(); // detaches now; the service disables the device when the connection drops
        base.OnFormClosed(e);
    }
}
