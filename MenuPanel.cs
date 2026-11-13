namespace VirtualScreenViewer;

/// <summary>
/// A small side panel inside the viewer with every setting in one place. It only edits
/// <see cref="ViewerSettings"/> and raises events; the viewer saves and applies them straight away.
/// Sizes are in 96-dpi units; the viewer scales them for the screen's DPI.
/// </summary>
internal sealed class MenuPanel : Panel
{
    public event Action? DisplayToggled, SettingsChanged, CloseClicked;

    private readonly Label _now = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 8), Tag = "dim" };
    private readonly CheckBox _on = Check("Virtual display on");
    private readonly ComboBox _resolution = Combo(), _refresh = Combo();
    private readonly CheckBox _fit = Check("Fit resolution to window");
    private readonly TextBox _hkMenu = new() { Width = 240, BorderStyle = BorderStyle.FixedSingle, ShortcutsEnabled = false };
    private ViewerSettings _s = new();
    private List<(int Width, int Height, int[] Rates)> _modes = new();
    private bool _syncing;

    public MenuPanel()
    {
        Width = 270;
        Dock = DockStyle.Right;
        Visible = false;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(12, 10, 12, 10),
        };
        var close = new Button { Text = "✕", FlatStyle = FlatStyle.Flat, Size = new Size(28, 26), Anchor = AnchorStyles.Right, Margin = new Padding(0) };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => CloseClicked?.Invoke();
        var header = new TableLayoutPanel { ColumnCount = 2, Width = 240, Height = 28, Margin = new Padding(0, 0, 0, 4) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = "Virtual Screen", AutoSize = true, Font = new Font(Font.FontFamily, 11f, FontStyle.Bold), Anchor = AnchorStyles.Left });
        header.Controls.Add(close);

        var winSettings = new Button { Text = "Windows display settings…", Width = 240, Height = 28, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 6, 0, 2) };
        winSettings.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:display") { UseShellExecute = true }); } catch { }
        };

        flow.Controls.AddRange(new Control[]
        {
            header, _now,
            Heading("Display"), _on, Caption("Resolution"), _resolution, Caption("Refresh rate"), _refresh, _fit, winSettings,
            Heading("Shortcut for this menu"), _hkMenu,
            new Label { Text = "Click the box and press the keys.\nThe title bar's right-click menu\nopens it too.", AutoSize = true, Margin = new Padding(0, 4, 0, 0), Tag = "dim" },
        });
        Controls.Add(flow);

        _on.CheckedChanged += (_, _) => { if (!_syncing) DisplayToggled?.Invoke(); };
        _resolution.SelectedIndexChanged += (_, _) => Changed();
        _refresh.SelectedIndexChanged += (_, _) => Changed();
        _fit.CheckedChanged += (_, _) => Changed();
        _hkMenu.KeyDown += CaptureHotkey;
        _hkMenu.TextChanged += (_, _) => Changed();
    }

    /// <summary>Shows the current state. Called whenever settings or the display change.</summary>
    public void Sync(ViewerSettings s, DisplayState state, string? problem)
    {
        _s = s;
        _syncing = true;
        try
        {
            var device = state == DisplayState.On ? VirtualDisplay.FindDevice() : null;
            var current = device is not null && VirtualDisplay.IsOn(device) ? VirtualDisplay.Current(device) : null;
            _now.Text = state switch
            {
                DisplayState.Starting => "Switching on…",
                DisplayState.Stopping => "Switching off…",
                DisplayState.On when current is { } c => $"Now {c.Width} × {c.Height} at {c.Hz} Hz",
                _ => problem is null ? "Off" : "Off: " + problem,
            };
            _now.MaximumSize = new Size(_hkMenu.Width, 0);
            _on.Checked = state is DisplayState.On or DisplayState.Starting;
            _on.Enabled = state is DisplayState.On or DisplayState.Off;

            if (_modes.Count == 0) _modes = VirtualDisplay.Modes();
            Fill(_resolution, "Keep Windows' setting", _modes.Select(m => $"{m.Width} × {m.Height}").Distinct(), s.Width > 0 ? $"{s.Width} × {s.Height}" : null);
            var rates = _modes.SelectMany(m => m.Rates).Append(s.RefreshRate).Where(r => r > 0).Distinct().OrderBy(r => r).Select(r => $"{r} Hz");
            Fill(_refresh, "Leave as Windows has it", rates, s.RefreshRate > 0 ? $"{s.RefreshRate} Hz" : null);
            _resolution.Enabled = !s.FitToWindow; // fit-to-window picks it
            _fit.Checked = s.FitToWindow;
            if (!_hkMenu.Focused) _hkMenu.Text = s.MenuHotkey;
        }
        finally { _syncing = false; }
    }

    /// <summary>Colours to match Windows' light or dark app setting.</summary>
    public void ApplyTheme(bool dark)
    {
        Color back = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
        Color field = dark ? Color.FromArgb(48, 48, 48) : Color.White;
        Color fore = dark ? Color.Gainsboro : Color.FromArgb(30, 30, 30);
        Color dim = dark ? Color.Gray : Color.FromArgb(100, 100, 100);
        Color accent = dark ? Color.FromArgb(255, 140, 0) : Color.FromArgb(190, 90, 0);
        Color border = dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(200, 200, 200);

        void Paint(Control c)
        {
            c.BackColor = c is ComboBox or TextBox or Button ? field : back;
            c.ForeColor = c.Tag as string switch { "dim" => dim, "heading" => accent, _ => fore };
            if (c is ComboBox combo) combo.FlatStyle = FlatStyle.Flat;
            if (c is Button b) b.FlatAppearance.BorderColor = b.FlatAppearance.BorderSize == 0 ? back : border;
            foreach (Control child in c.Controls) Paint(child);
        }
        Paint(this);
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
        SettingsChanged?.Invoke();
    }

    private static void Fill(ComboBox box, string first, IEnumerable<string> items, string? selected)
    {
        var all = new[] { first }.Concat(items).ToList();
        if (selected is not null && !all.Contains(selected)) all.Add(selected);
        if (!box.Items.Cast<string>().SequenceEqual(all)) { box.Items.Clear(); box.Items.AddRange(all.ToArray()); }
        box.SelectedIndex = selected is null ? 0 : box.Items.IndexOf(selected);
    }

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

    private static Label Heading(string text) => new()
    {
        Text = text.ToUpperInvariant(), AutoSize = true, Tag = "heading",
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8f, FontStyle.Bold), Margin = new Padding(0, 12, 0, 4),
    };
    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Tag = "dim", Margin = new Padding(0, 6, 0, 2) };
    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 4, 0, 2) };
    private static ComboBox Combo() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
}
