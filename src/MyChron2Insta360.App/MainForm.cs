using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using MyChron2Insta360.Core;

namespace MyChron2Insta360.App;

/// <summary>
/// Converter window: build up a list of RaceStudio "_CSV" folders / GPS csvs (add repeatedly, drop
/// several at once, or drop a parent folder), set the options, click Convert. By default each session
/// is written to its own file; tick "Merge" to combine them onto one timeline (via GPS iTOW).
/// </summary>
public sealed class MainForm : Form
{
    private readonly TextBox _sourcesDir = new() { Text = AppSettings.RaceStudioDataDir, PlaceholderText = AppSettings.RaceStudioDataDir };
    private readonly CheckBox _alwaysSourcesDir = new() { Text = "Make file picker always open this path", Checked = false };
    private readonly HashSet<string> _missingSourcesDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListBox _inputs = new() { SelectionMode = SelectionMode.MultiExtended, HorizontalScrollbar = true, IntegralHeight = false };
    private readonly Label _resolved = new() { AutoSize = false, ForeColor = Color.DimGray };
    private readonly TextBox _outDir = new() { PlaceholderText = "(same folder as input)" };
    private readonly NumericUpDown _nudge = new() { Minimum = -600, Maximum = 600, DecimalPlaces = 1, Increment = 0.5m };
    private readonly CheckBox _native = new() { Text = "Native rate (as recorded)", Checked = true };
    private readonly NumericUpDown _hz = new() { Minimum = 1, Maximum = 100, Value = 25 };
    private readonly NumericUpDown _maxAcc = new() { Minimum = 0, Maximum = 100, DecimalPlaces = 1, Increment = 0.5m, Value = 0 };
    private readonly CheckBox _gpx = new() { Text = "GPX", Checked = true };
    private readonly CheckBox _fit = new() { Text = "FIT", Checked = true };
    private readonly CheckBox _imuCsv = new() { Text = "IMU CSV", Checked = true };
    private readonly CheckBox _toCsv = new() { Text = "Telemetry Overlay", Checked = true };
    private readonly TextBox _iniPath = new() { PlaceholderText = "(MyChron2Insta360.ini, beside the exe)" };
    private readonly Label _formatHelp = new() { AutoSize = false, ForeColor = Color.DimGray };
    private readonly NumericUpDown _imuHz = new() { Minimum = 0, Maximum = 1000, Increment = 10, Value = 0 };
    private readonly CheckBox _tilt = new() { Text = "Correct mount tilt", Checked = false };
    private readonly TextBox _tiltDeg = new() { PlaceholderText = "auto", TextAlign = HorizontalAlignment.Right };
    private readonly Button _tiltDetect = new() { Text = "Autodetect" };
    private readonly Label _tiltHelp = new() { AutoSize = false, ForeColor = Color.DimGray };
    private readonly CheckBox _merge = new() { Text = "Merge sessions into one file", Checked = false };
    private readonly CheckBox _trim = new() { Text = "Trim standstill at start / end", Checked = false };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = Color.White };
    private readonly Button _convert = new() { Text = "Convert" };

    public MainForm(string[]? preload = null)
    {
        Text = "MyChron → Insta360";
        ClientSize = new Size(640, 886);
        MinimumSize = new Size(560, 620);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { /* keep default icon */ }
        AllowDrop = true;
        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += OnDragDrop;

        const int x = 16, labelW = 130, ctrlX = 150, ctrlW = 474;
        int y = 16;

        AddLabel("Default sources path:", x, y + 4, labelW);
        _sourcesDir.SetBounds(ctrlX, y, ctrlW - 90, 26);
        _sourcesDir.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        // A folder dropped here sets the path; without this the form would take it as a source.
        _sourcesDir.AllowDrop = true;
        _sourcesDir.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        _sourcesDir.DragDrop += OnDropSourcesDir;
        Controls.Add(_sourcesDir);
        AddRightButton("Browse…", ctrlX + ctrlW - 84, y - 1, 84, BrowseSourcesFolder);
        y += 30;
        _alwaysSourcesDir.SetBounds(ctrlX, y, 340, 24);
        Controls.Add(_alwaysSourcesDir);
        y += 34;

        AddLabel("Sources:", x, y + 4, labelW);
        _inputs.SetBounds(ctrlX, y, ctrlW, 74);
        _inputs.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _inputs.AllowDrop = true;
        _inputs.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        _inputs.DragDrop += OnDragDrop;
        Controls.Add(_inputs);
        y += 78;

        AddButton("Add folder…", ctrlX, y, 92, () => AddViaFolderDialog());
        AddButton("Add file(s)…", ctrlX + 96, y, 96, () => AddViaFileDialog());
        AddButton("Remove", ctrlX + 196, y, 80, RemoveSelected);
        AddButton("Clear", ctrlX + 280, y, 72, ClearInputs);
        y += 34;

        _resolved.SetBounds(ctrlX, y, ctrlW, 16);
        _resolved.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_resolved);
        y += 24;

        AddLabel("Output folder:", x, y + 4, labelW);
        _outDir.SetBounds(ctrlX, y, ctrlW - 90, 26);
        _outDir.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_outDir);
        AddRightButton("Browse…", ctrlX + ctrlW - 84, y - 1, 84, BrowseOutputFolder);
        y += 40;

        AddLabel("Time nudge (s):", x, y + 4, labelW);
        _nudge.SetBounds(ctrlX, y, 90, 26);
        Controls.Add(_nudge);
        AddLabel("fine-tune sync vs. camera clock", ctrlX + 104, y + 4, 300);
        y += 38;

        AddLabel("Output rate:", x, y + 4, labelW);
        _native.SetBounds(ctrlX, y + 2, 210, 22);
        _native.CheckedChanged += (_, __) => _hz.Enabled = !_native.Checked;
        Controls.Add(_native);
        y += 28;
        AddLabel("or fixed (Hz):", x, y + 4, labelW);
        _hz.SetBounds(ctrlX, y, 90, 26);
        _hz.Enabled = false;
        Controls.Add(_hz);
        AddLabel("downsample; used only when native is off", ctrlX + 104, y + 4, 320);
        y += 38;

        AddLabel("Max GPS accuracy:", x, y + 4, labelW);
        _maxAcc.SetBounds(ctrlX, y, 90, 26);
        Controls.Add(_maxAcc);
        AddLabel("metres; 0 = keep all fixes", ctrlX + 104, y + 4, 300);
        y += 38;

        AddLabel("Output format:", x, y + 4, labelW);
        _gpx.SetBounds(ctrlX, y, 60, 24);
        _fit.SetBounds(ctrlX + 64, y, 60, 24);
        _imuCsv.SetBounds(ctrlX + 128, y, 84, 24);
        _toCsv.SetBounds(ctrlX + 216, y, 140, 24);
        Controls.Add(_gpx);
        Controls.Add(_fit);
        Controls.Add(_imuCsv);
        Controls.Add(_toCsv);
        y += 26;
        _formatHelp.SetBounds(ctrlX, y, ctrlW, 68);
        _formatHelp.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _formatHelp.Text =
            "GPX: GPS track only (position, altitude, speed, time). Best for maps and Insta360.\r\n" +
            "FIT: GPS plus accelerometer/gyro, temperature and lap splits (where available).\r\n" +
            "IMU CSV: accelerometer (g) and gyro (deg/s) per sample, for analysis in other apps.\r\n" +
            "Telemetry Overlay: one wide CSV with every logged channel (MyChron, or a .vbo's GPS).";
        Controls.Add(_formatHelp);
        y += 72;

        AddLabel("IMU rate (Hz):", x, y + 4, labelW);
        _imuHz.SetBounds(ctrlX, y, 90, 26);
        Controls.Add(_imuHz);
        AddLabel("0 = native (Insta360 is ~1000 Hz)", ctrlX + 104, y + 4, 320);
        y += 36;

        // A logger angled on the steering wheel keeps a slice of gravity in its longitudinal channel.
        // Levelling it makes the accelerometer read braking, cornering and bumps on the right axes.
        AddLabel("Tilt correction:", x, y + 4, labelW);
        _tilt.SetBounds(ctrlX, y + 2, 140, 22);
        _tilt.CheckedChanged += (_, __) => UpdateTiltEnabled();
        Controls.Add(_tilt);
        _tiltDeg.SetBounds(ctrlX + 146, y, 56, 26);
        Controls.Add(_tiltDeg);
        AddLabel("°", ctrlX + 206, y + 4, 14);
        _tiltDetect.SetBounds(ctrlX + 224, y - 1, 92, 28);
        _tiltDetect.Click += async (_, __) => await DetectTiltAsync();
        Controls.Add(_tiltDetect);
        y += 28;
        _tiltHelp.SetBounds(ctrlX, y, ctrlW, 16);
        _tiltHelp.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _tiltHelp.Text = "leave the angle empty to measure it from the data during conversion";
        Controls.Add(_tiltHelp);
        UpdateTiltEnabled();
        y += 24;

        _merge.SetBounds(ctrlX, y, 340, 24);
        _merge.CheckedChanged += (_, __) => UpdateResolved();
        Controls.Add(_merge);
        y += 28;

        _trim.SetBounds(ctrlX, y, 340, 24);
        Controls.Add(_trim);
        y += 32;

        AddLabel("Settings file:", x, y + 4, labelW);
        _iniPath.SetBounds(ctrlX, y, ctrlW - 200, 26);
        _iniPath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_iniPath);
        AddRightButton("Save as default", ctrlX + ctrlW - 194, y - 1, 106, SaveDefaults);
        AddRightButton("Reset", ctrlX + ctrlW - 84, y - 1, 84, ResetDefaults);
        y += 34;

        _convert.SetBounds(ctrlX, y, 200, 38);
        _convert.Font = new Font(Font, FontStyle.Bold);
        _convert.Click += async (_, __) => await DoConvertAsync();
        Controls.Add(_convert);
        y += 50;

        _log.SetBounds(16, y, ClientSize.Width - 32, ClientSize.Height - y - 16);
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _log.Font = new Font("Consolas", 8.5F);
        Controls.Add(_log);

        // Taller than some screens: a 1080p laptop at 150% leaves this program 672 px. Open no taller
        // than the screen and scroll, rather than leave Convert under the taskbar.
        AutoScroll = true;
        AutoScrollMinSize = new Size(0, _log.Top + 60);
        Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
        if (Height > work.Height) ClientSize = new Size(ClientSize.Width, ClientSize.Height - (Height - work.Height));

        Log($"v{AppInfo.Version} ready. Add or drop MyChron .xrk / .xrz files, RaceStudio '_CSV' folders, " +
            "Dragy .vbo files, or Insta360 videos (.insv / .lrv).");

        // Saved defaults, if the user ever pressed "Save as default".
        var saved = AppSettings.Load(null, out string iniUsed, out string? iniError);
        if (iniError is not null)
        {
            Log($"Settings: {iniUsed}");
            Log($"  {iniError}");
            _iniPath.Text = iniUsed;   // so 'Save as default' writes back to where it was meant to go
        }
        else if (File.Exists(iniUsed))
        {
            ApplySettings(saved);
            // Show the path when it is not the default one, so it is clear where these came from.
            if (!string.Equals(iniUsed, AppSettings.DefaultPath, StringComparison.OrdinalIgnoreCase))
                _iniPath.Text = iniUsed;
            Log($"Loaded your saved settings from {iniUsed}");
        }

        if (preload is { Length: > 0 }) AddInputs(preload);
        // Start on the Sources list, as before, not in the path box with its text selected.
        ActiveControl = _inputs;
    }

    private void AddLabel(string text, int left, int top, int width) =>
        Controls.Add(new Label { Text = text, Left = left, Top = top, Width = width, AutoSize = false });

    private void AddButton(string text, int left, int top, int width, Action onClick)
    {
        var b = new Button { Text = text, Anchor = AnchorStyles.Top | AnchorStyles.Left };
        b.SetBounds(left, top, width, 28);
        b.Click += (_, __) => onClick();
        Controls.Add(b);
    }

    private void AddRightButton(string text, int left, int top, int width, Action onClick)
    {
        var b = new Button { Text = text, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        b.SetBounds(left, top, width, 28);
        b.Click += (_, __) => onClick();
        Controls.Add(b);
    }

    private static string DirName(string path) =>
        new DirectoryInfo(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;

    private List<string> InputPaths() => _inputs.Items.Cast<string>().ToList();

    private void AddInputs(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            var path = p.Trim();
            if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path))) continue;
            if (_inputs.Items.Cast<string>().Any(e => string.Equals(e, path, StringComparison.OrdinalIgnoreCase))) continue;
            _inputs.Items.Add(path);
        }
        UpdateResolved();
    }

    private void RemoveSelected()
    {
        for (int i = _inputs.SelectedIndices.Count - 1; i >= 0; i--)
            _inputs.Items.RemoveAt(_inputs.SelectedIndices[i]);
        UpdateResolved();
    }

    private void ClearInputs()
    {
        _inputs.Items.Clear();
        UpdateResolved();
    }

    private void AddViaFolderDialog()
    {
        try
        {
            string? folder = SourcePicker.PickFolder(this, "Add a folder of .xrk / .vbo files or Insta360 videos, a session '_CSV' folder, or a parent folder of several",
                                                     SourcesStart(), _alwaysSourcesDir.Checked);
            if (folder is not null) AddInputs(new[] { folder });
        }
        catch (Exception ex) { PickerFailed(ex); }
    }

    private void AddViaFileDialog()
    {
        // List the files this tool actually reads, so the dialog offers the supported sources only.
        const string logger = "*.xrk;*.xrz";
        const string myChron = "_GPS_o.csv;_GPS.csv;Accelerometer*.csv;Gyro*.csv;" +
                               "Logger Temperature.csv;_laps_and_splits.csv";
        const string video = "*.insv;*.lrv";
        const string vbo = "*.vbo";
        var filters = new[]
        {
            ("Supported telemetry", $"{logger};{myChron};{vbo};{video}"),
            ("MyChron logger file (*.xrk;*.xrz)", logger),
            ("MyChron / RaceStudio channels", myChron),
            ("Dragy / VBOX GPS log (*.vbo)", vbo),
            ("Insta360 video (*.insv;*.lrv)", video),
            ("All files (*.*)", "*.*"),
        };
        try
        {
            var files = SourcePicker.PickFiles(this, filters, SourcesStart(), _alwaysSourcesDir.Checked);
            if (files.Length > 0) AddInputs(files);
        }
        catch (Exception ex) { PickerFailed(ex); }
    }

    /// <summary>
    /// The pickers' default folder, or null to leave the choice to Windows when it is not there. That
    /// is said once per folder, not on every click.
    /// </summary>
    private string? SourcesStart()
    {
        string dir = AppSettings.SourcesDirOrDefault(_sourcesDir.Text);
        if (SourcePicker.UsableFolder(dir) is { } usable) return usable;
        if (_missingSourcesDirs.Add(dir))
            Log($"Default sources path not found, so the picker opens where Windows chooses: {dir}");
        return null;
    }

    private void PickerFailed(Exception ex)
    {
        Log("ERROR opening the file picker: " + ex.Message);
        MessageBox.Show(this, ex.Message, "File picker failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void OnDropSourcesDir(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } items) return;
        // A dropped file stands for the folder it is in.
        _sourcesDir.Text = Directory.Exists(items[0]) ? items[0] : Path.GetDirectoryName(items[0]) ?? items[0];
    }

    private void BrowseSourcesFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Pick the folder 'Add folder…' and 'Add file(s)…' open in" };
        string dir = AppSettings.SourcesDirOrDefault(_sourcesDir.Text);
        try { if (Directory.Exists(dir)) dlg.SelectedPath = dir; } catch { }
        if (dlg.ShowDialog(this) == DialogResult.OK) _sourcesDir.Text = dlg.SelectedPath;
    }

    /// <summary>Show how many GPS sessions the current list resolves to.</summary>
    private void UpdateResolved()
    {
        var paths = InputPaths();
        if (paths.Count == 0) { _resolved.Text = ""; return; }

        try
        {
            var clips = Insta360Trailer.DiscoverClips(paths);
            if (clips.Count > 0)
            {
                _resolved.ForeColor = Color.DimGray;
                string how = clips.Count > 1 ? (_merge.Checked ? ", merge into one" : ", one file each") : "";
                _resolved.Text = $"→ {clips.Count} Insta360 clip(s): GPS + IMU from the file metadata{how}";
                return;
            }
        }
        catch { /* fall through to the RaceStudio check */ }

        // Discovery only: an xrk is tens of megabytes, far too slow to parse on every list change.
        List<MyChronSources.Candidate> found;
        try { found = MyChronSources.Discover(paths); }
        catch (Exception e)
        {
            _resolved.ForeColor = Color.Firebrick;
            _resolved.Text = "→ " + e.Message.Split('\n')[0];
            return;
        }
        _resolved.ForeColor = Color.DimGray;
        string mode = _merge.Checked ? "merge into one" : "one file each";
        // Name the file kinds actually used, so dropping an .xrz does not report "xrk".
        var kinds = found.Select(f => f.Kind).Distinct().OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
        _resolved.Text = $"→ {found.Count} session(s) found, from {string.Join(" + ", kinds)} ({mode})";
    }

    /// <summary>
    /// Merge one session into a single wide CSV for Telemetry Overlay. The IMU is passed in already
    /// levelled, so the tilt correction reaches this export just as it reaches GPX and FIT. Returns
    /// the file written.
    /// </summary>
    private string WriteTelemetryOverlay(MyChronSource src, IReadOnlyList<ImuSample> imu,
                                         string basePath, DateTime? startUtc)
    {
        var channels = src.LoadChannels();
        var laps = src.LoadLaps();
        var opt = new TelemetryOverlayOptions
        {
            // An xrk knows the track, racer and championship; a CSV export does not.
            SessionName = src.Info?.Track is { Length: > 0 } track ? track : src.Gps.Name,
            Racer = src.Info?.Racer ?? "",
            Vehicle = src.Info?.Vehicle ?? "",
            Championship = src.Info?.Championship ?? "",
            StartLocal = startUtc is DateTime utc
                ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local)
                : src.Info?.StartLocal,
        };
        string path = basePath + "_TelemetryOverlay.csv";
        Log(channels.Count > 0 ? $"  Telemetry Overlay CSV, {channels.Count} channel(s) found:"
                               : "  Telemetry Overlay CSV, GPS only:");
        TelemetryOverlayCsv.Write(path, src.Gps, imu, channels, laps, opt, Log);
        return path;
    }

    /// <summary>The current control values, as the settings file records them.</summary>
    private AppSettings CollectSettings()
    {
        var formats = new List<string>();
        if (_gpx.Checked) formats.Add("gpx");
        if (_fit.Checked) formats.Add("fit");
        if (_toCsv.Checked) formats.Add("telemetry-overlay");
        string typedTilt = _tiltDeg.Text.Trim();
        return new AppSettings
        {
            Format = string.Join(",", formats),
            ImuCsv = _imuCsv.Checked,
            OutDir = _outDir.Text.Trim(),
            Nudge = (double)_nudge.Value,
            NativeRate = _native.Checked,
            Hz = (double)_hz.Value,
            MaxAccuracy = (double)_maxAcc.Value,
            ImuHz = (double)_imuHz.Value,
            Tilt = !_tilt.Checked ? "off" : typedTilt.Length == 0 ? "auto" : typedTilt,
            Merge = _merge.Checked,
            Trim = _trim.Checked,
            SourcesDir = AppSettings.SourcesDirOrDefault(_sourcesDir.Text),
            AlwaysOpenSourcesDir = _alwaysSourcesDir.Checked,
        };
    }

    private void ApplySettings(AppSettings s)
    {
        var f = s.Format.Split(new[] { ',', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim().ToLowerInvariant()).ToList();
        _gpx.Checked = f.Contains("gpx") || f.Contains("both");
        _fit.Checked = f.Contains("fit") || f.Contains("both");
        _toCsv.Checked = f.Contains("telemetry-overlay") || f.Contains("telemetryoverlay") || f.Contains("to");
        _imuCsv.Checked = s.ImuCsv;
        _outDir.Text = s.OutDir;
        _nudge.Value = Clamp(_nudge, s.Nudge);
        _native.Checked = s.NativeRate;
        _hz.Value = Clamp(_hz, s.Hz);
        _maxAcc.Value = Clamp(_maxAcc, s.MaxAccuracy);
        _imuHz.Value = Clamp(_imuHz, s.ImuHz);

        bool off = s.Tilt.Equals("off", StringComparison.OrdinalIgnoreCase) || s.Tilt.Length == 0;
        _tilt.Checked = !off;
        _tiltDeg.Text = off || s.Tilt.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "" : s.Tilt;
        _merge.Checked = s.Merge;
        _trim.Checked = s.Trim;
        _sourcesDir.Text = s.SourcesDir;
        _alwaysSourcesDir.Checked = s.AlwaysOpenSourcesDir;

        UpdateTiltEnabled();
        UpdateResolved();
    }

    /// <summary>A saved value outside a control's range must not throw on load.</summary>
    private static decimal Clamp(NumericUpDown c, double v) => Math.Clamp((decimal)v, c.Minimum, c.Maximum);

    private void SaveDefaults()
    {
        try
        {
            CollectSettings().Save(_iniPath.Text, out string used);
            Log($"Saved as the default: {used}");
        }
        catch (Exception ex)
        {
            Log("ERROR saving the settings: " + ex.Message);
            MessageBox.Show(this,
                $"Could not write the settings file:\n\n{ex.Message}\n\n" +
                "If this program sits somewhere you cannot write to, put a writable path in the " +
                "'Settings file' box and try again.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ResetDefaults()
    {
        ApplySettings(new AppSettings());
        Log("Back to the built-in defaults.");

        string path = AppSettings.Resolve(string.IsNullOrWhiteSpace(_iniPath.Text) ? null : _iniPath.Text.Trim());
        bool stub = !string.Equals(path, AppSettings.DefaultPath, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(AppSettings.DefaultPath);
        if (!File.Exists(path) && !stub) return;

        // The saved file would otherwise come back on the next start, so offer to remove it, and ask
        // first because it is the user's file.
        if (MessageBox.Show(this, $"Also delete the saved settings file?\n\n{path}",
                            "Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            Log("Kept the saved file. Press 'Save as default' to overwrite it with these settings.");
            return;
        }
        try
        {
            if (File.Exists(path)) { File.Delete(path); Log($"Deleted {path}"); }
            // Remove the pointer too, or the next start would look for a file that is gone.
            if (stub) { File.Delete(AppSettings.DefaultPath); Log($"Deleted {AppSettings.DefaultPath}"); }
            _iniPath.Text = "";
        }
        catch (Exception ex) { Log("ERROR deleting the settings file: " + ex.Message); }
    }

    private void UpdateTiltEnabled()
    {
        _tiltDeg.Enabled = _tilt.Checked;
        _tiltDetect.Enabled = _tilt.Checked;
        _tiltHelp.Enabled = _tilt.Checked;
    }

    /// <summary>Read the tilt controls. Null means the typed angle is not a number.</summary>
    private TiltPlan? BuildTiltPlan()
    {
        string typed = _tiltDeg.Text.Trim();
        if (!_tilt.Checked || typed.Length == 0) return new TiltPlan { Enabled = _tilt.Checked };
        double? manual = Cli.ParseD(typed);
        return manual is null ? null : new TiltPlan { Enabled = true, ManualDeg = manual };
    }

    /// <summary>Measure the mount tilt from the current inputs and fill it into the angle field.</summary>
    private async Task DetectTiltAsync()
    {
        var inputs = InputPaths();
        if (inputs.Count == 0)
        {
            MessageBox.Show(this, "Add an input first: the tilt is measured from its accelerometer data.",
                            Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _tiltDetect.Enabled = false;
        Log("Measuring the mount tilt …");
        try
        {
            var found = await Task.Run(() =>
            {
                var e = TiltPlan.DetectFrom(inputs, out string s, out string? w);
                return (Est: e, Source: s, Why: w);
            });
            if (found.Est is null)
            {
                Log($"  Could not measure the tilt: {found.Why}.");
                MessageBox.Show(this, $"Could not measure the tilt: {found.Why}.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _tiltDeg.Text = found.Est.AngleDeg.ToString("0.0", CultureInfo.InvariantCulture);
            Log($"Mount tilt measured from {found.Source}:");
            foreach (var line in TiltPlan.Describe(found.Est)) Log(line);
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Tilt detection failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { _tiltDetect.Enabled = _tilt.Checked; }
    }

    private void BrowseOutputFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Pick the output folder for the GPX/FIT files" };
        try { if (Directory.Exists(_outDir.Text)) dlg.SelectedPath = _outDir.Text; } catch { }
        if (dlg.ShowDialog(this) == DialogResult.OK) _outDir.Text = dlg.SelectedPath;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } items)
        {
            AddInputs(items);
            Log($"Added {items.Length} item(s).");
        }
    }

    private void Log(string msg)
    {
        if (_log.IsHandleCreated && _log.InvokeRequired) { _log.BeginInvoke(() => Log(msg)); return; }
        _log.AppendText(msg + Environment.NewLine);
    }

    /// <summary>Convert Insta360 clips via the native trailer parser, individually or merged.</summary>
    private async Task ConvertInsta360Async(List<Insta360Clip> clips, bool gpx, bool fit, bool imuCsv, double imuHz,
                                            bool merge, TiltPlan tilt)
    {
        string? outDir = string.IsNullOrWhiteSpace(_outDir.Text) ? null : _outDir.Text.Trim();
        _convert.Enabled = false;
        Log($"Found {clips.Count} Insta360 clip(s), reading …");
        try
        {
            var written = await Task.Run(() =>
            {
                var files = new List<string>();
                if (outDir is not null) Directory.CreateDirectory(outDir);

                // Clips are independent, so read and encode them in parallel. The degree is capped
                // because each clip holds its whole IMU stream in memory.
                int par = Math.Max(1, Math.Min(clips.Count, Math.Min(4, Environment.ProcessorCount)));
                var slots = new (Insta360Telemetry Tel, string File)?[clips.Count];
                Parallel.For(0, clips.Count, new ParallelOptions { MaxDegreeOfParallelism = par }, i =>
                {
                    var t = Insta360Trailer.ParseClip(clips[i], out string used);
                    if (t.Gps.Count > 0)
                    {
                        DateTime g0 = t.Gps[0].Utc;
                        foreach (var s in t.Imu) s.Utc = g0.AddSeconds(s.ElapsedS);
                    }
                    slots[i] = (t, used);
                });

                var loaded = new List<(Insta360Telemetry Tel, string File)>();
                foreach (var s in slots)
                {
                    if (s is not { } l) continue;
                    Log($"  {Path.GetFileName(l.File)}: {l.Tel.Gps.Count} GPS points, {l.Tel.Imu.Count} IMU samples");
                    if (l.Tel.Gps.Count == 0 && l.Tel.Imu.Count == 0)
                    { Log("    no telemetry (was a GPS remote or the phone app connected while recording?)"); continue; }
                    loaded.Add(l);
                }
                if (loaded.Count == 0) return files;
                loaded.Sort((a, b) => (a.Tel.Gps.Count > 0 ? a.Tel.Gps[0].Utc : DateTime.MaxValue)
                               .CompareTo(b.Tel.Gps.Count > 0 ? b.Tel.Gps[0].Utc : DateTime.MaxValue));
                foreach (var l in loaded) tilt.ApplyTo(l.Tel.Imu, Log);

                if (merge && loaded.Count > 1)
                {
                    var segs = loaded.Where(l => l.Tel.Gps.Count > 0).Select(l => l.Tel.Gps).ToList();
                    var imu = new List<ImuSample>();
                    foreach (var l in loaded) imu.AddRange(l.Tel.Imu);
                    imu.Sort((a, b) => (a.Utc ?? DateTime.MinValue).CompareTo(b.Utc ?? DateTime.MinValue));
                    if (imuHz > 0) imu = Cli.DownsampleByUtc(imu, imuHz);
                    DateTime? start = segs.Count > 0 ? segs[0][0].Utc : imu.FirstOrDefault()?.Utc;
                    string baseName = "merged-" + (start is DateTime st ? st.ToString("yyyyMMdd-HHmmss") : "unknown");
                    string dir = outDir ?? Path.GetDirectoryName(loaded[0].File)!;
                    string basePath = Path.Combine(dir, baseName);
                    string name = $"{baseName} ({loaded.Count} clips)";
                    Log($"Merging {loaded.Count} clips, {segs.Sum(s => s.Count)} GPS points.");
                    if (segs.Count > 0)
                    {
                        if (gpx) { var p = basePath + ".gpx"; GpxWriter.Write(p, segs, name); files.Add(p); }
                        if (fit) { var p = basePath + ".fit"; FitWriter.Write(p, segs, name, new FitWriter.Extras { Imu = imu.Count > 0 ? imu : null }); files.Add(p); }
                    }
                    if (imuCsv && imu.Count > 0) { var p = basePath + "_imu.csv"; ImuCsvWriter.Write(p, imu); files.Add(p); }
                }
                else
                {
                    var per = new List<string>[loaded.Count];
                    Parallel.For(0, loaded.Count, new ParallelOptions { MaxDegreeOfParallelism = par }, i =>
                    {
                        var (tel, used) = loaded[i];
                        var made = per[i] = new List<string>();
                        string dir = outDir ?? Path.GetDirectoryName(used)!;
                        string baseName = Path.GetFileNameWithoutExtension(used);
                        string basePath = Path.Combine(dir, baseName);
                        var imu = imuHz > 0 ? ImuCsvWriter.Downsample(tel.Imu, imuHz) : tel.Imu;
                        if (tel.Gps.Count > 0)
                        {
                            var segs = new List<List<TrackPoint>> { tel.Gps };
                            if (gpx) { var p = basePath + ".gpx"; GpxWriter.Write(p, segs, baseName); made.Add(p); }
                            if (fit) { var p = basePath + ".fit"; FitWriter.Write(p, segs, baseName, new FitWriter.Extras { Imu = imu.Count > 0 ? imu : null }); made.Add(p); }
                        }
                        if (imuCsv && imu.Count > 0) { var p = basePath + "_imu.csv"; ImuCsvWriter.Write(p, imu); made.Add(p); }
                    });
                    foreach (var m in per) if (m is not null) files.AddRange(m);
                }
                return files;
            });

            foreach (var w in written) Log($"Wrote {w}");
            if (written.Count == 0)
                MessageBox.Show(this, "No telemetry could be extracted from the selected video(s).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
                MessageBox.Show(this, $"Wrote {written.Count} file(s):\n" + string.Join("\n", written), "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Conversion failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { _convert.Enabled = true; }
    }

    private async Task DoConvertAsync()
    {
        var inputs = InputPaths();
        if (inputs.Count == 0)
        {
            MessageBox.Show(this, "Add at least one source: an .xrk / .xrz, a RaceStudio '_CSV' folder, a .vbo, an Insta360 video, or a folder holding them.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        bool gpx = _gpx.Checked, fit = _fit.Checked, imuCsv = _imuCsv.Checked, toCsv = _toCsv.Checked;
        if (!gpx && !fit && !imuCsv && !toCsv)
        {
            MessageBox.Show(this, "Select at least one output (GPX, FIT, IMU CSV or Telemetry Overlay).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        double imuHz = (double)_imuHz.Value;

        var tiltPlan = BuildTiltPlan();
        if (tiltPlan is null)
        {
            MessageBox.Show(this, $"'{_tiltDeg.Text}' is not a valid tilt angle.\n\nLeave the field empty to measure it from the data.",
                            Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Insta360 video input (files or folders holding them) takes the native trailer path.
        var clips = Insta360Trailer.DiscoverClips(inputs);
        if (clips.Count > 0)
        {
            // That export merges a RaceStudio channel folder and uses the MyChron axis frame, so it
            // does not apply to camera telemetry.
            if (toCsv) Log("Note: the Telemetry Overlay CSV is a MyChron export; skipping it for Insta360 input.");
            await ConvertInsta360Async(clips, gpx, fit, imuCsv, imuHz, _merge.Checked, tiltPlan);
            return;
        }

        var opt = new ConversionOptions
        {
            ManualOffsetSeconds = (double)_nudge.Value,
            TargetHz = _native.Checked ? 0 : (double)_hz.Value,
            MaxAccuracyM = (double)_maxAcc.Value,
            TrimStandstill = _trim.Checked,
        };
        string? outDir = string.IsNullOrWhiteSpace(_outDir.Text) ? null : _outDir.Text.Trim();
        bool merge = _merge.Checked;

        _convert.Enabled = false;
        Log($"Loading {inputs.Count} input(s) …");
        try
        {
            var result = await Task.Run(() =>
            {
                var sources = MyChronSources.LoadAll(inputs, log: Log);
                var sessions = sources.Select(s => s.Gps).ToList();
                var sessionLines = sources.Select(s => "  " + s.Describe()).ToList();
                foreach (var d in inputs.Where(i => File.Exists(i) && VboReader.IsDlapFile(i))) Log(VboReader.DlapNote(d));
                if (imuCsv && sources.Any(s => s.FromVbo)) Log(VboReader.NoImuNote);
                if (outDir is not null) Directory.CreateDirectory(outDir);

                var written = new List<string>();
                int totalPoints = 0;
                bool merged = merge && sources.Count > 1;

                if (merged)
                {
                    var segs = TrackBuilder.Build(sessions, opt);
                    totalPoints = opt.TotalPoints;
                    string first = inputs[0];
                    string baseName = Cli.MergedBaseName(opt);
                    string dir = outDir ?? (Directory.Exists(first) ? first : Path.GetDirectoryName(sessions[0].SourceFile)!);
                    string basePath = Path.Combine(dir, baseName);
                    string name = $"{baseName} ({sessions.Count} sessions)";
                    var allImu = new List<ImuSample>();
                    foreach (var src in sources)
                    {
                        DateTime? startUtc = TrackBuilder.SessionStartUtc(src.Gps, opt);
                        var im = src.LoadImu();
                        tiltPlan.ApplyTo(im, Log);
                        if (imuHz > 0) im = ImuCsvWriter.Downsample(im, imuHz);
                        // Each session on its own clock: unstamped, the FIT and the IMU CSV would place
                        // every session's IMU from the first one's start.
                        if (startUtc is DateTime su) foreach (var v in im) v.Utc = su.AddSeconds(v.ElapsedS);
                        // The AiM container describes one session, so this export stays per session
                        // even when the GPX and FIT are merged.
                        if (toCsv)
                            written.Add(WriteTelemetryOverlay(src, im, Path.Combine(dir, src.BaseName), startUtc));
                        // A session with no clock cannot be placed on the merged timeline.
                        if (startUtc is not null) allImu.AddRange(im);
                    }
                    allImu.Sort((a, b) => (a.Utc ?? DateTime.MinValue).CompareTo(b.Utc ?? DateTime.MinValue));
                    if (gpx) { var p = basePath + ".gpx"; GpxWriter.Write(p, segs, name); written.Add(p); }
                    if (fit)
                    {
                        var p = basePath + ".fit";
                        FitWriter.Write(p, segs, name, new FitWriter.Extras { Imu = allImu.Count > 0 ? allImu : null });
                        written.Add(p);
                    }
                    if (imuCsv && allImu.Count > 0) { var p = basePath + "_imu.csv"; ImuCsvWriter.Write(p, allImu); written.Add(p); }
                }
                else
                {
                    foreach (var src in sources)
                    {
                        var segs = TrackBuilder.Build(new[] { src.Gps }, opt);
                        totalPoints += opt.TotalPoints;
                        string dir = outDir ?? src.OutputFolder;
                        string basePath = Path.Combine(dir, src.BaseName);

                        // IMU, laps and temperature ride along into the FIT and the IMU CSV, timed from
                        // the session's own start, which trimming the track does not move.
                        DateTime? startUtc = TrackBuilder.SessionStartUtc(src.Gps, opt);
                        var imu = src.LoadImu();
                        tiltPlan.ApplyTo(imu, Log);   // level before any downsample, so every output agrees
                        if (imuHz > 0) imu = ImuCsvWriter.Downsample(imu, imuHz);
                        if (startUtc is DateTime su0) foreach (var v in imu) v.Utc = su0.AddSeconds(v.ElapsedS);

                        var laps = new List<LapSplit>();
                        if (startUtc is DateTime lapStart)
                            foreach (var (number, start, duration) in src.LoadLaps())
                                laps.Add(new LapSplit { Number = number, StartUtc = lapStart.AddSeconds(start), DurationS = duration });
                        var ex = new FitWriter.Extras
                        {
                            Imu = imu.Count > 0 ? imu : null,
                            Laps = laps,
                            Temperature = MyChronSources.Temperature(src.LoadChannels(), startUtc),
                        };

                        if (gpx) { var p = basePath + ".gpx"; GpxWriter.Write(p, segs, src.Gps.Name); written.Add(p); }
                        if (fit) { var p = basePath + ".fit"; FitWriter.Write(p, segs, src.Gps.Name, ex); written.Add(p); }
                        if (imuCsv && imu.Count > 0) { var p = basePath + "_imu.csv"; ImuCsvWriter.Write(p, imu); written.Add(p); }
                        // Same levelled IMU stream the FIT and the IMU CSV get, never re-read from disk.
                        if (toCsv) written.Add(WriteTelemetryOverlay(src, imu, basePath, startUtc));
                    }
                }
                return (count: sessions.Count, sessionLines, written, totalPoints, merged);
            });

            Log($"Found {result.count} session(s):");
            foreach (var s in result.sessionLines) Log(s);
            Log($"Time base: {opt.DescribeTimeBase()}");
            Log($"Output rate: {opt.EffectiveHz:0.#} Hz {(opt.Downsampled ? "(downsampled)" : "(native)")}");
            foreach (var o in opt.Overlaps)
                Log($"Warning: {o}. They are two recordings of the same stretch, which one merged file " +
                    "shows twice; untick Merge to convert them separately.");
            Log(result.merged
                ? $"Merged → {result.totalPoints} track points."
                : $"{result.count} file set(s), {result.totalPoints} track points total.");
            if (result.merged && opt.StartUtc is DateTime a0 && opt.EndUtc is DateTime a1)
                Log($"UTC span: {a0:yyyy-MM-dd HH:mm:ss}Z .. {a1:HH:mm:ss}Z");
            foreach (var w in result.written) Log($"Wrote {w}");
            Log("Import in Insta360: Dashboard → Data Source → Local Files → Import.");

            if (result.totalPoints == 0)
                MessageBox.Show(this, "No GPS points were produced. Check the accuracy filter or the inputs.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
                MessageBox.Show(this, $"Wrote {result.written.Count} file(s):\n" + string.Join("\n", result.written), "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
