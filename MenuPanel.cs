namespace VirtualScreenViewer;

/// <summary>
/// A small side panel inside the viewer with every setting in one place. Changes apply straight
/// away: settings are saved to the JSON file, which the viewer reloads and applies.
/// </summary>
internal sealed class MenuPanel : Panel
{
    public event Action? DisplayToggled, ControlToggled, MoveWindowClicked, SettingsChanged, CloseClicked;

    private static readonly Color Back = Color.FromArgb(32, 32, 32), Fore = Color.Gainsboro, Dim = Color.Gray, Field = Color.FromArgb(48, 48, 48);

    private readonly Label _now = new() { AutoSize = true, ForeColor = Dim, Margin = new Padding(0, 0, 0, 8) };
    private readonly CheckBox _on = Check("Virtual display on");
    private readonly ComboBox _resolution = Combo(), _refresh = Combo();
    private readonly CheckBox _fit = Check("Fit resolution to window");
    private readonly CheckBox _control = Check("Control mode (clicks go to it)");
    private readonly Button _move = Btn("Move last active window to/from it");
    private readonly TextBox _hkMenu = Hotkey(), _hkShow = Hotkey(), _hkMove = Hotkey(), _hkControl = Hotkey();
    private ViewerSettings _s = new();
    private List<(int Width, int Height, int[] Rates)> _modes = new();
    private bool _syncing;

    public MenuPanel()
    {
        Width = 270;
        Dock = DockStyle.Right;
        BackColor = Back;
        ForeColor = Fore;
        Visible = false;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(12, 10, 12, 10),
        };
        var close = new Button { Text = "✕", FlatStyle = FlatStyle.Flat, Size = new Size(28, 26), ForeColor = Fore, Anchor = AnchorStyles.Right, Margin = new Padding(0) };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => CloseClicked?.Invoke();
        var header = new TableLayoutPanel { ColumnCount = 2, Width = 240, Height = 28, Margin = new Padding(0, 0, 0, 4) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = "Virtual Screen", AutoSize = true, Font = new Font(Font.FontFamily, 11f, FontStyle.Bold), Anchor = AnchorStyles.Left });
        header.Controls.Add(close);

        flow.Controls.Add(header);
        flow.Controls.Add(_now);
        flow.Controls.Add(Heading("Display"));
        flow.Controls.Add(_on);
        flow.Controls.Add(Caption("Resolution"));
        flow.Controls.Add(_resolution);
        flow.Controls.Add(Caption("Refresh rate"));
        flow.Controls.Add(_refresh);
        flow.Controls.Add(_fit);
        var winSettings = Btn("Windows display settings…");
        winSettings.Click += (_, _) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:display") { UseShellExecute = true }); } catch { } };
        flow.Controls.Add(winSettings);

        flow.Controls.Add(Heading("Viewer"));
        flow.Controls.Add(_control);
        flow.Controls.Add(_move);

        flow.Controls.Add(Heading("Hotkeys"));
        foreach (var (label, box) in new[] { ("This menu", _hkMenu), ("Show/hide viewer", _hkShow), ("Move active window", _hkMove), ("Control mode", _hkControl) })
        {
            flow.Controls.Add(Caption(label));
            flow.Controls.Add(box);
        }
        flow.Controls.Add(new Label { Text = "Click a box and press the keys.\nBackspace clears it.", AutoSize = true, ForeColor = Dim, Margin = new Padding(0, 4, 0, 0) });
        Controls.Add(flow);

        _on.CheckedChanged += (_, _) => { if (!_syncing) DisplayToggled?.Invoke(); };
        _control.CheckedChanged += (_, _) => { if (!_syncing) ControlToggled?.Invoke(); };
        _move.Click += (_, _) => MoveWindowClicked?.Invoke();
        _resolution.SelectedIndexChanged += (_, _) => Changed();
        _refresh.SelectedIndexChanged += (_, _) => Changed();
        _fit.CheckedChanged += (_, _) => Changed();
        foreach (var box in new[] { _hkMenu, _hkShow, _hkMove, _hkControl })
        {
            box.KeyDown += CaptureHotkey;
            box.TextChanged += (_, _) => Changed();
        }
    }

    /// <summary>Shows the current state. Called whenever settings or the display change.</summary>
    public void Sync(ViewerSettings s, string? device, bool busy, bool control)
    {
        _s = s;
        _syncing = true;
        try
        {
            bool attached = device is not null && VirtualDisplay.IsOn(device);
            var current = attached ? VirtualDisplay.Current(device!) : null;
            _now.Text = busy ? "Switching…" : current is { } c ? $"Now {c.Width} × {c.Height} at {c.Hz} Hz" : "Off";
            _on.Checked = attached;
            _on.Enabled = !busy;

            if (_modes.Count == 0) _modes = VirtualDisplay.Modes();
            Fill(_resolution, "Keep Windows' setting", _modes.Select(m => $"{m.Width} × {m.Height}").Distinct(), s.Width > 0 ? $"{s.Width} × {s.Height}" : null);
            var rates = _modes.SelectMany(m => m.Rates).Append(s.RefreshRate).Where(r => r > 0).Distinct().OrderBy(r => r).Select(r => $"{r} Hz");
            Fill(_refresh, "Leave as Windows has it", rates, s.RefreshRate > 0 ? $"{s.RefreshRate} Hz" : null);
            _resolution.Enabled = !s.FitToWindow; // fit-to-window picks it
            _fit.Checked = s.FitToWindow;
            _control.Checked = control;
            _move.Enabled = attached;
            SetIfIdle(_hkMenu, s.MenuHotkey);
            SetIfIdle(_hkShow, s.ShowHideHotkey);
            SetIfIdle(_hkMove, s.MoveWindowHotkey);
            SetIfIdle(_hkControl, s.ControlHotkey);
        }
        finally { _syncing = false; }
    }

    private void Changed()
    {
        if (_syncing) return;
        if (_resolution.SelectedIndex <= 0) _s.Width = _s.Height = 0;
        else
        {
            var p = ((string)_resolution.SelectedItem!).Split('×');
            _s.Width = int.Parse(p[0].Trim());
            _s.Height = int.Parse(p[1].Trim());
        }
        _s.RefreshRate = _refresh.SelectedIndex <= 0 ? 0 : int.Parse(((string)_refresh.SelectedItem!).Split(' ')[0]);
        _s.FitToWindow = _fit.Checked;
        _resolution.Enabled = !_s.FitToWindow;
        _s.MenuHotkey = _hkMenu.Text;
        _s.ShowHideHotkey = _hkShow.Text;
        _s.MoveWindowHotkey = _hkMove.Text;
        _s.ControlHotkey = _hkControl.Text;
        SettingsChanged?.Invoke();
    }

    private static void Fill(ComboBox box, string first, IEnumerable<string> items, string? selected)
    {
        var all = new[] { first }.Concat(items).ToList();
        if (selected is not null && !all.Contains(selected)) all.Add(selected);
        if (!box.Items.Cast<string>().SequenceEqual(all)) { box.Items.Clear(); box.Items.AddRange(all.ToArray()); }
        box.SelectedIndex = selected is null ? 0 : box.Items.IndexOf(selected);
    }

    private static void SetIfIdle(TextBox box, string text) { if (!box.Focused) box.Text = text; }

    private static void CaptureHotkey(object? sender, KeyEventArgs e)
    {
        var box = (TextBox)sender!;
        e.SuppressKeyPress = true;
        if (e.KeyCode is Keys.Back or Keys.Delete && e.Modifiers == Keys.None) { box.Text = ""; return; }
        if (e.KeyCode is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin || e.Modifiers == Keys.None) return;
        var parts = new List<string>();
        if (e.Control) parts.Add("Ctrl");
        if (e.Alt) parts.Add("Alt");
        if (e.Shift) parts.Add("Shift");
        var key = e.KeyCode.ToString();
        parts.Add(key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) ? key[1..] : key);
        box.Text = string.Join("+", parts);
    }

    // ---------- Small styled controls ----------

    private static Label Heading(string text) => new()
    {
        Text = text.ToUpperInvariant(), AutoSize = true, ForeColor = Color.FromArgb(255, 140, 0),
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8f, FontStyle.Bold), Margin = new Padding(0, 12, 0, 4),
    };
    private static Label Caption(string text) => new() { Text = text, AutoSize = true, ForeColor = Dim, Margin = new Padding(0, 6, 0, 2) };
    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, ForeColor = Fore, Margin = new Padding(0, 4, 0, 2) };
    private static ComboBox Combo() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, BackColor = Field, ForeColor = Fore, FlatStyle = FlatStyle.Flat };
    private static TextBox Hotkey() => new() { Width = 240, BackColor = Field, ForeColor = Fore, BorderStyle = BorderStyle.FixedSingle, ShortcutsEnabled = false };
    private static Button Btn(string text)
    {
        var b = new Button { Text = text, Width = 240, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Field, ForeColor = Fore, Margin = new Padding(0, 6, 0, 2) };
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 70);
        return b;
    }
}
