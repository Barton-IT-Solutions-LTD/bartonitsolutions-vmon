namespace VirtualScreenViewer;

/// <summary>Settings dialog: the modes come from the driver's vdd_settings.xml, so only working ones are offered.</summary>
internal sealed class SettingsForm : Form
{
    private readonly ViewerSettings _settings;
    private readonly ComboBox _resolution = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly ComboBox _refresh = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly CheckBox _fit = new() { Text = "Fit resolution to the window's shape when resized", AutoSize = true };
    private readonly TextBox _showHide = new() { Width = 220 }, _move = new() { Width = 220 }, _control = new() { Width = 220 };

    public SettingsForm(ViewerSettings settings)
    {
        _settings = settings;
        Text = "Virtual Screen settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var modes = VirtualDisplay.Modes();
        _resolution.Items.Add("Keep Windows' setting");
        foreach (var m in modes.Select(m => (m.Width, m.Height)).Distinct()) _resolution.Items.Add($"{m.Width} × {m.Height}");
        _resolution.SelectedIndex = Math.Max(0, _resolution.Items.IndexOf($"{settings.Width} × {settings.Height}"));

        _refresh.Items.Add("Leave as Windows has it");
        var rates = modes.SelectMany(m => m.Rates).Append(settings.RefreshRate).Where(r => r > 0).Distinct().OrderBy(r => r);
        foreach (var r in rates) _refresh.Items.Add($"{r} Hz");
        _refresh.SelectedIndex = Math.Max(0, _refresh.Items.IndexOf($"{settings.RefreshRate} Hz"));

        _fit.Checked = settings.FitToWindow;
        _showHide.Text = settings.ShowHideHotkey;
        _move.Text = settings.MoveWindowHotkey;
        _control.Text = settings.ControlHotkey;
        foreach (var box in new[] { _showHide, _move, _control }) box.KeyDown += CaptureHotkey;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        void Row(string label, Control c)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) });
            grid.Controls.Add(c);
        }
        Row("Resolution", _resolution);
        Row("Refresh rate", _refresh);
        grid.Controls.Add(_fit); grid.SetColumnSpan(_fit, 2);
        Row("Show/hide viewer", _showHide);
        Row("Move active window", _move);
        Row("Control mode", _control);
        var hint = new Label { Text = "Click a hotkey box and press the keys. Delete or Backspace clears it.", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 0, 6) };
        grid.Controls.Add(hint); grid.SetColumnSpan(hint, 2);

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) => Apply();
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        grid.Controls.Add(buttons); grid.SetColumnSpan(buttons, 2);

        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;
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

    private void Apply()
    {
        if (_resolution.SelectedIndex <= 0) { _settings.Width = _settings.Height = 0; }
        else
        {
            var p = ((string)_resolution.SelectedItem!).Split('×');
            _settings.Width = int.Parse(p[0].Trim());
            _settings.Height = int.Parse(p[1].Trim());
        }
        _settings.RefreshRate = _refresh.SelectedIndex <= 0 ? 0 : int.Parse(((string)_refresh.SelectedItem!).Split(' ')[0]);
        _settings.FitToWindow = _fit.Checked;
        _settings.ShowHideHotkey = _showHide.Text;
        _settings.MoveWindowHotkey = _move.Text;
        _settings.ControlHotkey = _control.Text;
    }
}
