using System.Drawing;
using System.Windows.Forms;
using MyChron2Insta360.Core;

namespace MyChron2Insta360.App;

/// <summary>
/// Simple converter window: choose (or drag-drop) a CSV, set timezone/nudge/rate/format, click
/// Convert. Writes GPX and/or FIT next to the input, ready for the Insta360 Stats Dashboard.
/// </summary>
public sealed class MainForm : Form
{
    private readonly TextBox _input = new();
    private readonly ComboBox _tz = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _nudge = new() { Minimum = -600, Maximum = 600, DecimalPlaces = 1, Increment = 0.5m };
    private readonly CheckBox _matchGps = new() { Text = "Match source GPS rate (auto)", Checked = true };
    private readonly NumericUpDown _hz = new() { Minimum = 1, Maximum = 100, Value = 25 };
    private readonly NumericUpDown _minSat = new() { Minimum = 0, Maximum = 20, Value = 0 };
    private readonly CheckBox _gpx = new() { Text = "GPX", Checked = true };
    private readonly CheckBox _fit = new() { Text = "FIT", Checked = true };
    private readonly CheckBox _trim = new() { Text = "Trim standstill at start / end", Checked = false };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = Color.White };
    private readonly Button _convert = new() { Text = "Convert" };

    public MainForm(string? preload = null)
    {
        Text = "MyChron → Insta360";
        ClientSize = new Size(640, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        AllowDrop = true;
        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += OnDragDrop;

        const int x = 16, labelW = 130, ctrlX = 150, ctrlW = 474;
        int y = 16;

        AddLabel("CSV file:", x, y + 4, labelW);
        _input.SetBounds(ctrlX, y, ctrlW - 96, 26);
        _input.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_input);
        var browse = new Button { Text = "Browse…", Anchor = AnchorStyles.Top | AnchorStyles.Right };
        browse.SetBounds(ctrlX + ctrlW - 88, y - 1, 88, 28);
        browse.Click += (_, __) => Browse();
        Controls.Add(browse);
        y += 40;

        AddLabel("Session timezone:", x, y + 4, labelW);
        _tz.SetBounds(ctrlX, y, ctrlW, 26);
        _tz.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_tz);
        y += 40;

        AddLabel("Time nudge (s):", x, y + 4, labelW);
        _nudge.SetBounds(ctrlX, y, 90, 26);
        Controls.Add(_nudge);
        AddLabel("fine-tune sync vs. camera clock", ctrlX + 104, y + 4, 300);
        y += 38;

        AddLabel("Output rate:", x, y + 4, labelW);
        _matchGps.SetBounds(ctrlX, y + 2, 220, 22);
        _matchGps.CheckedChanged += (_, __) => _hz.Enabled = !_matchGps.Checked;
        Controls.Add(_matchGps);
        y += 28;
        AddLabel("or fixed (Hz):", x, y + 4, labelW);
        _hz.SetBounds(ctrlX, y, 90, 26);
        _hz.Enabled = false;
        Controls.Add(_hz);
        AddLabel("used only when auto is off", ctrlX + 104, y + 4, 300);
        y += 38;

        AddLabel("Min satellites:", x, y + 4, labelW);
        _minSat.SetBounds(ctrlX, y, 90, 26);
        Controls.Add(_minSat);
        AddLabel("0 = keep all points", ctrlX + 104, y + 4, 300);
        y += 38;

        AddLabel("Output format:", x, y + 4, labelW);
        _gpx.SetBounds(ctrlX, y, 70, 24);
        _fit.SetBounds(ctrlX + 78, y, 70, 24);
        Controls.Add(_gpx);
        Controls.Add(_fit);
        y += 34;

        _trim.SetBounds(ctrlX, y, 320, 24);
        Controls.Add(_trim);
        y += 40;

        _convert.SetBounds(ctrlX, y, 200, 38);
        _convert.Font = new Font(Font, FontStyle.Bold);
        _convert.Click += async (_, __) => await DoConvertAsync();
        Controls.Add(_convert);
        y += 50;

        _log.SetBounds(16, y, ClientSize.Width - 32, ClientSize.Height - y - 16);
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _log.Font = new Font("Consolas", 8.5F);
        Controls.Add(_log);

        PopulateTimezones();

        if (!string.IsNullOrWhiteSpace(preload))
        {
            _input.Text = preload;
            Log("Loaded: " + preload);
        }
        Log($"v{AppInfo.Version} ready. Drag a MyChron CSV here or click Browse…");
    }

    private void AddLabel(string text, int left, int top, int width) =>
        Controls.Add(new Label { Text = text, Left = left, Top = top, Width = width, AutoSize = false });

    private void PopulateTimezones()
    {
        _tz.Items.Add("Automatic (from GPS position)");
        _tz.Items.Add($"System local: {TimeZoneInfo.Local.Id}");
        foreach (var tz in TimeZoneInfo.GetSystemTimeZones())
            _tz.Items.Add(tz.Id);
        _tz.SelectedIndex = 0; // Automatic
    }

    /// <summary>Build options from the form. <paramref name="usingAuto"/> = timezone auto-detect selected.</summary>
    private ConversionOptions BuildOptions(out bool usingAuto)
    {
        var opt = new ConversionOptions
        {
            ManualOffsetSeconds = (double)_nudge.Value,
            TargetHz = _matchGps.Checked ? 0 : (double)_hz.Value,
            MinSatellites = (int)_minSat.Value,
            TrimStandstill = _trim.Checked,
        };

        usingAuto = false;
        if (_tz.SelectedIndex <= 0) { usingAuto = true; opt.AutoTimeZone = true; }         // Automatic
        else if (_tz.SelectedIndex == 1) { opt.AutoTimeZone = false; }                      // System local
        else
        {
            opt.AutoTimeZone = false;
            try { opt.SessionTimeZone = TimeZoneInfo.FindSystemTimeZoneById((string)_tz.SelectedItem!); }
            catch { /* fall back to local */ }
        }
        return opt;
    }

    private void Browse()
    {
        using var dlg = new OpenFileDialog { Filter = "AiM / MyChron CSV (*.csv)|*.csv|All files (*.*)|*.*" };
        try { if (!string.IsNullOrWhiteSpace(_input.Text)) dlg.InitialDirectory = Path.GetDirectoryName(_input.Text); }
        catch { /* ignore bad path */ }
        if (dlg.ShowDialog(this) == DialogResult.OK) _input.Text = dlg.FileName;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            _input.Text = files[0];
            Log("Dropped: " + files[0]);
        }
    }

    private void Log(string msg)
    {
        if (_log.IsHandleCreated && _log.InvokeRequired) { _log.BeginInvoke(() => Log(msg)); return; }
        _log.AppendText(msg + Environment.NewLine);
    }

    private async Task DoConvertAsync()
    {
        string input = _input.Text.Trim();
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
        {
            MessageBox.Show(this, "Please choose a valid CSV file.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        bool gpx = _gpx.Checked, fit = _fit.Checked;
        if (!gpx && !fit)
        {
            MessageBox.Show(this, "Select at least one output format (GPX or FIT).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var opt = BuildOptions(out bool usingAuto);

        _convert.Enabled = false;
        Log($"Parsing {Path.GetFileName(input)} …");
        try
        {
            var result = await Task.Run(() =>
            {
                var data = AimCsvParser.Parse(input);
                var pts = TrackBuilder.Build(data, opt);
                string name = data.GetMeta("Session") ?? Path.GetFileNameWithoutExtension(input);
                var written = new List<string>();
                if (gpx) { var p = Path.ChangeExtension(input, ".gpx"); GpxWriter.Write(p, pts, name); written.Add(p); }
                if (fit) { var p = Path.ChangeExtension(input, ".fit"); FitWriter.Write(p, pts, name); written.Add(p); }
                return (rows: data.Rows.Count, count: pts.Count,
                        first: pts.Count > 0 ? pts[0].Utc : (DateTime?)null,
                        last: pts.Count > 0 ? pts[^1].Utc : (DateTime?)null,
                        written);
            });

            string zone = opt.SessionTimeZone?.Id ?? TimeZoneInfo.Local.Id;
            Log($"Timezone: {zone}{(usingAuto ? " (auto-detected from GPS)" : "")}");
            Log($"Output rate: {opt.EffectiveHz:0.#} Hz" +
                (opt.DetectedGpsHz is double gd ? $" (detected GPS {gd:0.#} Hz)" : " (GPS rate not detected)"));
            Log($"Parsed {result.rows} rows → {result.count} track points.");
            if (result.first is DateTime f && result.last is DateTime l)
                Log($"UTC span: {f:yyyy-MM-dd HH:mm:ss}Z .. {l:HH:mm:ss}Z");
            foreach (var w in result.written) Log($"Wrote {w}");
            Log("Import in Insta360: Dashboard → Data Source → Local Files → Import.");

            if (result.count == 0)
                MessageBox.Show(this, "No GPS points were produced. Check the satellite filter or the CSV channels.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
                MessageBox.Show(this, $"Wrote {result.count} track points:\n" + string.Join("\n", result.written),
                    "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Conversion failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _convert.Enabled = true;
        }
    }
}
