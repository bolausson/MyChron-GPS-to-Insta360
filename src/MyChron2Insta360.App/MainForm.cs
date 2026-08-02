using System.Drawing;
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
    private readonly Label _formatHelp = new() { AutoSize = false, ForeColor = Color.DimGray };
    private readonly NumericUpDown _imuHz = new() { Minimum = 0, Maximum = 1000, Increment = 10, Value = 0 };
    private readonly CheckBox _merge = new() { Text = "Merge sessions into one file", Checked = false };
    private readonly CheckBox _trim = new() { Text = "Trim standstill at start / end", Checked = false };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = Color.White };
    private readonly Button _convert = new() { Text = "Convert" };

    public MainForm(string[]? preload = null)
    {
        Text = "MyChron → Insta360";
        ClientSize = new Size(640, 720);
        MinimumSize = new Size(560, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { /* keep default icon */ }
        AllowDrop = true;
        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += OnDragDrop;

        const int x = 16, labelW = 130, ctrlX = 150, ctrlW = 474;
        int y = 16;

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
        _imuCsv.SetBounds(ctrlX + 128, y, 90, 24);
        Controls.Add(_gpx);
        Controls.Add(_fit);
        Controls.Add(_imuCsv);
        y += 26;
        _formatHelp.SetBounds(ctrlX, y, ctrlW, 52);
        _formatHelp.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _formatHelp.Text =
            "GPX: GPS track only (position, altitude, speed, time). Best for maps and Insta360.\r\n" +
            "FIT: GPS plus accelerometer/gyro, temperature and lap splits (where available).\r\n" +
            "IMU CSV: accelerometer (g) and gyro (deg/s) per sample, for analysis in other apps.";
        Controls.Add(_formatHelp);
        y += 56;

        AddLabel("IMU rate (Hz):", x, y + 4, labelW);
        _imuHz.SetBounds(ctrlX, y, 90, 26);
        Controls.Add(_imuHz);
        AddLabel("0 = native (Insta360 is ~1000 Hz)", ctrlX + 104, y + 4, 320);
        y += 36;

        _merge.SetBounds(ctrlX, y, 340, 24);
        _merge.CheckedChanged += (_, __) => UpdateResolved();
        Controls.Add(_merge);
        y += 28;

        _trim.SetBounds(ctrlX, y, 340, 24);
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

        if (preload is { Length: > 0 }) AddInputs(preload);
        Log($"v{AppInfo.Version} ready. Add or drop RaceStudio '_CSV' folders, or Insta360 videos (.insv / .lrv).");
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
        using var dlg = new FolderBrowserDialog { Description = "Add a session '_CSV' folder (or a parent folder of several)" };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddInputs(new[] { dlg.SelectedPath });
    }

    private void AddViaFileDialog()
    {
        // List the files this tool actually reads, so the dialog offers the supported sources only.
        const string myChron = "_GPS_o.csv;_GPS.csv;Accelerometer*.csv;Gyro*.csv;" +
                               "Logger Temperature.csv;_laps_and_splits.csv";
        const string video = "*.insv;*.lrv";
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = $"Supported telemetry|{myChron};{video}|" +
                     $"MyChron / RaceStudio channels|{myChron}|" +
                     $"Insta360 video (*.insv;*.lrv)|{video}|" +
                     "All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddInputs(dlg.FileNames);
    }

    /// <summary>Show how many GPS sessions the current list resolves to.</summary>
    private void UpdateResolved()
    {
        var paths = InputPaths();
        if (paths.Count == 0) { _resolved.Text = ""; return; }

        int videos = paths.Count(p => File.Exists(p) && Insta360Trailer.HasTrailer(p));
        if (videos > 0)
        {
            _resolved.ForeColor = Color.DimGray;
            _resolved.Text = $"→ {videos} Insta360 video(s): GPS + IMU read from the file's metadata";
            return;
        }

        int total = 0, bad = 0;
        foreach (var p in paths)
        {
            try { total += RaceStudioGpsCsv.DiscoverGpsFiles(p).Count; }
            catch { bad++; }
        }
        if (total == 0)
        {
            _resolved.ForeColor = Color.Firebrick;
            _resolved.Text = "→ no _GPS_o.csv / _GPS.csv found in the listed item(s)";
            return;
        }
        _resolved.ForeColor = bad > 0 ? Color.DarkGoldenrod : Color.DimGray;
        string mode = _merge.Checked ? "merge into one" : "one file each";
        _resolved.Text = $"→ {total} session(s) found ({mode})" + (bad > 0 ? $" , {bad} item(s) unreadable" : "");
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

    /// <summary>Convert Insta360 video file(s) via the native trailer parser.</summary>
    private async Task ConvertInsta360Async(List<string> videos, bool gpx, bool fit, bool imuCsv, double imuHz)
    {
        string? outDir = string.IsNullOrWhiteSpace(_outDir.Text) ? null : _outDir.Text.Trim();
        _convert.Enabled = false;
        try
        {
            var written = await Task.Run(() =>
            {
                var files = new List<string>();
                foreach (var v in videos)
                {
                    var tel = Insta360Trailer.Parse(v);
                    Log($"{Path.GetFileName(v)}: {tel.Model ?? "Insta360"}, {tel.Gps.Count} GPS points, {tel.Imu.Count} IMU samples");
                    if (tel.Gps.Count == 0 && tel.Imu.Count == 0)
                    { Log("  no telemetry (was a GPS remote or the phone app connected while recording?)"); continue; }

                    if (tel.Gps.Count > 0)
                        foreach (var s in tel.Imu) s.Utc = tel.Gps[0].Utc.AddSeconds(s.ElapsedS);

                    string dir = outDir ?? Path.GetDirectoryName(Path.GetFullPath(v))!;
                    if (outDir is not null) Directory.CreateDirectory(outDir);
                    string baseName = Path.GetFileNameWithoutExtension(v);
                    string basePath = Path.Combine(dir, baseName);
                    var imu = imuHz > 0 ? ImuCsvWriter.Downsample(tel.Imu, imuHz) : tel.Imu;

                    if (tel.Gps.Count > 0)
                    {
                        var segs = new List<List<TrackPoint>> { tel.Gps };
                        Log($"  UTC span: {tel.Gps[0].Utc:yyyy-MM-dd HH:mm:ss}Z .. {tel.Gps[^1].Utc:HH:mm:ss}Z");
                        if (gpx) { var p = basePath + ".gpx"; GpxWriter.Write(p, segs, baseName); files.Add(p); }
                        if (fit)
                        {
                            var p = basePath + ".fit";
                            FitWriter.Write(p, segs, baseName, new FitWriter.Extras { Imu = imu.Count > 0 ? imu : null });
                            files.Add(p);
                        }
                    }
                    else Log("  no GPS in this file; skipping GPX/FIT");

                    if (imuCsv && imu.Count > 0)
                    {
                        var p = basePath + "_imu.csv";
                        ImuCsvWriter.Write(p, imu);
                        files.Add(p);
                        Log($"  IMU: {imu.Count} samples written");
                    }
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
            MessageBox.Show(this, "Add at least one RaceStudio '_CSV' folder (or a parent folder) or a _GPS csv.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        bool gpx = _gpx.Checked, fit = _fit.Checked, imuCsv = _imuCsv.Checked;
        if (!gpx && !fit && !imuCsv)
        {
            MessageBox.Show(this, "Select at least one output (GPX, FIT or IMU CSV).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        double imuHz = (double)_imuHz.Value;

        // Insta360 video input takes the native trailer path instead of the RaceStudio CSV pipeline.
        var videos = inputs.Where(p => File.Exists(p) && Insta360Trailer.HasTrailer(p)).ToList();
        if (videos.Count > 0)
        {
            await ConvertInsta360Async(videos, gpx, fit, imuCsv, imuHz);
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
                var sessions = RaceStudioGpsCsv.LoadAll(inputs);
                var sessionLines = sessions.Select(s => $"  {DirName(Path.GetDirectoryName(s.SourceFile)!)}, {s.Samples.Count} fixes, {s.NativeHz:0.#} Hz").ToList();
                if (outDir is not null) Directory.CreateDirectory(outDir);

                var written = new List<string>();
                int totalPoints = 0;
                bool merged = merge && sessions.Count > 1;

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
                    foreach (var s in sessions)
                    {
                        var im = RaceStudioImu.Load(Path.GetDirectoryName(s.SourceFile)!);
                        if (imuHz > 0) im = ImuCsvWriter.Downsample(im, imuHz);
                        allImu.AddRange(im);
                    }
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
                    foreach (var s in sessions)
                    {
                        var segs = TrackBuilder.Build(new[] { s }, opt);
                        totalPoints += opt.TotalPoints;
                        string folder = Path.GetDirectoryName(s.SourceFile)!;
                        string baseName = DirName(folder);
                        string dir = outDir ?? folder;
                        string basePath = Path.Combine(dir, baseName);

                        // IMU, laps and temperature ride along into the FIT and the IMU CSV.
                        DateTime? startUtc = opt.StartUtc;
                        var imu = RaceStudioImu.Load(folder);
                        if (imuHz > 0) imu = ImuCsvWriter.Downsample(imu, imuHz);
                        if (startUtc is DateTime su0) foreach (var v in imu) v.Utc = su0.AddSeconds(v.ElapsedS);
                        var ex = new FitWriter.Extras
                        {
                            Imu = imu.Count > 0 ? imu : null,
                            Laps = RaceStudioChannels.LoadLaps(folder, startUtc),
                            Temperature = RaceStudioChannels.LoadTemperature(folder, startUtc),
                        };

                        if (gpx) { var p = basePath + ".gpx"; GpxWriter.Write(p, segs, s.Name); written.Add(p); }
                        if (fit) { var p = basePath + ".fit"; FitWriter.Write(p, segs, s.Name, ex); written.Add(p); }
                        if (imuCsv && imu.Count > 0) { var p = basePath + "_imu.csv"; ImuCsvWriter.Write(p, imu); written.Add(p); }
                    }
                }
                return (count: sessions.Count, sessionLines, written, totalPoints, merged);
            });

            Log($"Found {result.count} session(s):");
            foreach (var s in result.sessionLines) Log(s);
            Log($"Time base: {(opt.UsedItow ? "iTOW (exact GPS time)" : "relative, sync will need a nudge")}");
            Log($"Output rate: {opt.EffectiveHz:0.#} Hz{(opt.TargetHz > 0 ? " (downsampled)" : " (native)")}");
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
