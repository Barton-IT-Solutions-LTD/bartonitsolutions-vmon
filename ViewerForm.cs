using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Win32;
using static VirtualScreenViewer.Native;

namespace VirtualScreenViewer;

/// <summary>A window that shows the virtual display live, like a monitor in a window.</summary>
public sealed class ViewerForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private Screen? _target;
    private Bitmap? _frame;
    private bool _turnedOn; // we switched the virtual display on, so switch it off again on close
    private ViewerSettings _settings;
    private string? _startupProblem;
    private readonly FileSystemWatcher _settingsWatcher;
    private readonly System.Windows.Forms.Timer _settingsDebounce = new() { Interval = 300 };

    public ViewerForm()
    {
        Text = "Virtual Screen";
        ClientSize = new Size(1280, 720);
        MinimumSize = new Size(320, 200);
        BackColor = Color.Black;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _settings = ViewerSettings.Load(out var problem);
        if (problem is not null) _startupProblem = $"{ViewerSettings.FilePath}:\n{problem}";
        if (VirtualDisplay.FindDevice() is { } device)
        {
            try
            {
                if (!VirtualDisplay.IsOn(device)) { VirtualDisplay.TurnOn(device, _settings); _turnedOn = true; }
                else VirtualDisplay.UseSettings(device, _settings);
            }
            catch (Exception ex) { _startupProblem ??= ex.Message; }
        }
        Shown += (_, _) => { if (_startupProblem is not null) ShowError(_startupProblem); };

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

        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        FindTarget();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    // ---------- Title bar follows the OS light/dark setting ----------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTheme();

        var menu = GetSystemMenu(Handle, false);
        AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(menu, MF_STRING, ID_CONNECTED, "Virtual display connected");
        AppendMenuW(menu, MF_STRING, ID_SETTINGS, "Edit settings (refresh rate, resolution)...");
    }

    // ---------- Title bar right-click menu ----------

    // System menu command IDs must keep the low four bits clear.
    private const uint ID_CONNECTED = 0x10;
    private const uint ID_SETTINGS = 0x20;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INITMENUPOPUP && m.WParam == GetSystemMenu(Handle, false))
            UpdateMenu(m.WParam);
        else if (m.Msg == WM_SYSCOMMAND && ((uint)m.WParam & 0xFFF0) == ID_CONNECTED)
        {
            ToggleConnected();
            return;
        }
        else if (m.Msg == WM_SYSCOMMAND && ((uint)m.WParam & 0xFFF0) == ID_SETTINGS)
        {
            EditSettings();
            return;
        }
        base.WndProc(ref m);
    }

    // ---------- Settings file ----------

    private void EditSettings()
    {
        if (!File.Exists(ViewerSettings.FilePath)) ViewerSettings.Load(out _); // writes the defaults
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{ViewerSettings.FilePath}\"")); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    /// <summary>Applies the settings file again after it's saved (if the virtual display is on).</summary>
    private void ReloadSettings()
    {
        _settings = ViewerSettings.Load(out var problem);
        if (problem is not null) { ShowError($"{ViewerSettings.FilePath}:\n{problem}"); return; }
        if (VirtualDisplay.FindDevice() is not { } device || !VirtualDisplay.IsOn(device)) return;
        try { VirtualDisplay.UseSettings(device, _settings); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private static void UpdateMenu(IntPtr menu)
    {
        var device = VirtualDisplay.FindDevice();
        CheckMenuItem(menu, ID_CONNECTED, MF_BYCOMMAND | (device is not null && VirtualDisplay.IsOn(device) ? MF_CHECKED : MF_UNCHECKED));
        EnableMenuItem(menu, ID_CONNECTED, MF_BYCOMMAND | (device is not null ? MF_ENABLED : MF_GRAYED));
    }

    private void ToggleConnected()
    {
        if (VirtualDisplay.FindDevice() is not { } device) return;
        _turnedOn = false; // the user's choice now; leave it alone on close
        try
        {
            if (VirtualDisplay.IsOn(device)) VirtualDisplay.TurnOff(device);
            else VirtualDisplay.TurnOn(device, _settings);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "Virtual display", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void OnUserPreferenceChanged(object? s, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General) BeginInvoke(ApplyTheme);
    }

    private void ApplyTheme()
    {
        int dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is 0 ? 1 : 0;
        DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    // ---------- Which display to show ----------

    private void OnDisplaysChanged(object? s, EventArgs e) => BeginInvoke(FindTarget);

    /// <summary>The Virtual Display Driver's monitor, if it's connected.</summary>
    private void FindTarget()
    {
        var device = VirtualDisplay.FindDevice();
        _target = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == device);
        Invalidate();
    }

    private bool ShowingOwnScreen => _target is not null && _target.DeviceName == Screen.FromControl(this).DeviceName;

    // ---------- Capture ----------

    private void Tick()
    {
        if (_target is null || WindowState == FormWindowState.Minimized || ShowingOwnScreen) return;
        Capture(_target.Bounds);
        Invalidate();
    }

    private new void Capture(Rectangle b)
    {
        if (_frame is null || _frame.Size != b.Size)
        {
            _frame?.Dispose();
            _frame = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppRgb);
        }

        using var g = Graphics.FromImage(_frame);
        IntPtr dst = g.GetHdc();
        IntPtr src = GetDC(IntPtr.Zero);
        try
        {
            BitBlt(dst, 0, 0, b.Width, b.Height, src, b.X, b.Y, SRCCOPY);
            DrawCursor(dst, b);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, src);
            g.ReleaseHdc(dst);
        }
    }

    private static void DrawCursor(IntPtr hdc, Rectangle b)
    {
        var ci = new CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0) return;
        if (!b.Contains(ci.ptScreenPos.X, ci.ptScreenPos.Y)) return;

        int hx = 0, hy = 0;
        if (GetIconInfo(ci.hCursor, out var ii))
        {
            hx = ii.xHotspot; hy = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
        DrawIconEx(hdc, ci.ptScreenPos.X - b.X - hx, ci.ptScreenPos.Y - b.Y - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
    }

    // ---------- Rendering ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);

        string? message = null;
        if (_target is null)
            message = VirtualDisplay.FindDevice() is null
                ? "No virtual display found.\n\nInstall the Virtual Display Driver (see README)."
                : "The virtual display is disconnected.\n\nRight-click the title bar to connect it.";
        else if (ShowingOwnScreen)
            message = "This window is on the display it's showing.\nMove it to another screen.";

        if (message is not null || _frame is null)
        {
            TextRenderer.DrawText(g, message ?? "Waiting for display…", Font, ClientRectangle, Color.Gainsboro,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_frame, Fit(_frame.Size, ClientSize));
    }

    private static Rectangle Fit(Size src, Size box)
    {
        double scale = Math.Min((double)box.Width / src.Width, (double)box.Height / src.Height);
        int w = (int)(src.Width * scale), h = (int)(src.Height * scale);
        return new Rectangle((box.Width - w) / 2, (box.Height - h) / 2, w, h);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _settingsWatcher.Dispose();
        _settingsDebounce.Dispose();
        _timer.Dispose();
        _frame?.Dispose();
        if (_turnedOn && VirtualDisplay.FindDevice() is { } device)
            try { VirtualDisplay.TurnOff(device); } catch { }
        base.OnFormClosed(e);
    }
}
