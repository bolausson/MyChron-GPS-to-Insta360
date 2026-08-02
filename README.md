# MyChron-GPS-to-Insta360

<img src="assets/icon/png/icon-128.png" alt="MyChron2Insta360 app icon" align="right" width="120">

**Vibe coded!**

Convert **RaceStudio3 GPS CSV exports** (from **MyChron** GoKart lap timers) into **GPX** and **FIT**
files that the **Insta360 app / Insta360 Studio "Stats Dashboard"** can overlay onto your action-cam
video: speed, altitude, distance, gradient and a track map, all synced automatically to the footage.

It also works the other way round: point it at an **Insta360 video** (`.insv` / `.lrv`) and it extracts
the **GPS and IMU** recorded by the camera into GPX, FIT and CSV for use in other apps.

A single Windows `.exe`, with no runtime to install. **Drag the export folder onto it**, or
**double-click** to pick it, or drive it from the **command line**.

## Download

⬇️ **[Latest release: `MyChron2Insta360.exe`](https://github.com/bolausson/MyChron-GPS-to-Insta360/releases/latest/download/MyChron2Insta360.exe)**.
It is a single self-contained Windows 11 binary (no .NET install needed). That link always points at the
newest build. You can also browse [all releases](https://github.com/bolausson/MyChron-GPS-to-Insta360/releases).

## Getting the CSV export from RaceStudio3

The single "AiM CSV" export resamples everything to one rate and has no true GPS time. Instead, use the
per-channel CSV export, which gives GPS at its native rate plus a GPS-time column. Enable it once:

> **Preferences, Data Download Preferences, Advanced Tab, Additional Export Formats,**
> tick **"CSV (Comma Separated Values)"**

![RaceStudio3 Data Download Preferences: the Advanced tab with "CSV (Comma Separated Values)" ticked under Additional Export Formats](media/RaceStudio3-Preferences.png)

Now exporting a session writes a **`..._CSV`** folder containing one file per channel. This tool reads
the GPS files from it:

- **`_GPS_o.csv`**: raw receiver rate (**25 Hz** on MyChron 6). *Default.*
- **`_GPS.csv`**: a 10 Hz decimation.

Columns: `time, itow, lat, lon, alt, speed, accuracy`, where **`itow`** is GPS time-of-week.

## Reading an Insta360 video

Drop an **`.insv`** or **`.lrv`** file in and the tool reads the camera's own metadata trailer natively
(no ExifTool or other helper needed), giving:

- **GPS** at the rate the source provided (about 10 Hz), already stamped with absolute UTC.
- **IMU**: accelerometer and gyroscope at roughly 1000 Hz.

The X-series cameras have no built-in GPS, so a clip only contains GPS if a **GPS Preview Remote**, a
**GPS Action Remote**, or the phone app was connected **while recording**. Adding a track afterwards in
the Insta360 app is only a dashboard overlay and does not write GPS into the file. Telemetry may sit in
the `.insv` or in the matching `.lrv`, so if one comes up empty, try the other.

## What each output holds

| Output | Contents |
|---|---|
| **GPX** | GPS track only: position, altitude, per-point speed and time. Best for maps and Insta360. |
| **FIT** | GPS track plus accelerometer and gyroscope, temperature and lap splits (where the source has them). |
| **IMU CSV** | Accelerometer (g) and gyroscope (deg/s), one row per sample. Same columns for both sources. |

The IMU CSV is written by default; untick **IMU CSV** in the GUI or pass `--no-imu-csv` to skip it. Its
columns are `utc, elapsed_s, accel_x_g, accel_y_g, accel_z_g, gyro_x_dps, gyro_y_dps, gyro_z_dps`. Note
that each device uses its own axis directions, so MyChron and Insta360 axes are not directly comparable.

Because the Insta360 IMU runs near 1000 Hz, a full session makes a large CSV (about 70 MB for 17
minutes). Use **IMU rate (Hz)** in the GUI or `--imu-hz` to downsample, for example `--imu-hz 50`.

## Why this is accurate

Insta360 syncs telemetry to video **by absolute timestamp**. The `itow` column is real GPS time, so the
tool derives **exact UTC to the millisecond**. There is no timezone to configure and no guessing (the
calendar date is read from the RaceStudio folder path). A `--nudge` / *Time nudge* control remains for
trimming any residual camera-clock offset.

## Multiple sessions

The MyChron starts a **new session** whenever the kart slows or stops, so one continuous video can span
several sessions (several `_CSV` folders). Point the tool at a **parent folder** and it finds them all.
Because every point has exact GPS time, they line up on one timeline automatically.

- **Default:** each session becomes its own output file.
- **Merge** (CLI `--merge` / GUI "Merge sessions into one file"): all sessions combined into a single
  output, with one GPX track segment per session so no line is drawn across the stopped gaps.

Output names: an individual file takes its session folder's name. A merged file is
**`merged-YYYYMMDD-HHMMSS`** built from the earliest GPS timestamp (UTC). Override with `--out`, or
place the files elsewhere with `--out-dir`.

## Features

- Reads a RaceStudio `_CSV` folder, a **parent folder of several**, a `_GPS*.csv`, or an **Insta360
  `.insv` / `.lrv`** video, all auto-detected.
- **Exact UTC** from GPS `itow` (MyChron) or from the camera's GPS records (Insta360), so no timezone
  configuration.
- Keeps the **native rates** (GPS 25 Hz, IMU 50 Hz on MyChron, about 1000 Hz on Insta360), each with an
  optional downsample.
- Extracts **accelerometer and gyroscope**, plus **temperature** and **lap splits** from MyChron.
- Optional **merge** of multiple sessions, accuracy filter, standstill trim, and **output folder**.
- Writes **GPX 1.1** (`gpxtpx` speed), **FIT** (official Garmin SDK) and an **IMU CSV**.
- **CLI + GUI** in one binary.

## Build

Requires the **.NET 10 SDK** (`winget install Microsoft.DotNet.SDK.10`).

```powershell
# Build a single self-contained .exe (no .NET needed on the target machine)
dotnet publish src/MyChron2Insta360.App -c Release
# output: src/MyChron2Insta360.App/bin/Release/net10.0-windows/win-x64/publish/MyChron2Insta360.exe
```

## Usage

**GUI:** double-click `MyChron2Insta360.exe`, then drag the `..._CSV` folder onto it (or use *Folder…*).

**CLI:**

```powershell
MyChron2Insta360 "...\Session_..._CSV"                       # one session, GPX, native 25 Hz
MyChron2Insta360 "...\Casteluccio" --format both             # parent folder, one file per session
MyChron2Insta360 "...\Casteluccio" --merge --out-dir C:\out  # merge all sessions into one file
MyChron2Insta360 "...\_GPS_o.csv" --hz 10 --nudge -1.5
MyChron2Insta360 "...\VID_20260801_171316_00_005.insv" --both      # Insta360 video: GPS + IMU
MyChron2Insta360 "...\LRV_20260801_171316_01_005.lrv" --imu-hz 50  # downsample the 1 kHz IMU
MyChron2Insta360 --help
```

| Option | Meaning |
|---|---|
| `--merge` | Merge all sessions into ONE output (default: one file per session) |
| `-o, --out <file>` | Explicit output path, single output only (extension set per format) |
| `--out-dir <folder>` | Write outputs into this folder (created if needed) |
| `--format <gpx\|fit\|both>` | Output format(s). Default `gpx` (`--fit` / `--both` are shortcuts) |
| `--gps10` | Use `_GPS.csv` (10 Hz) instead of `_GPS_o.csv` (25 Hz) |
| `--hz <n>` | Downsample to n Hz (default: native rate) |
| `--nudge <seconds>` | Shift every timestamp to fine-tune sync |
| `--max-accuracy <m>` | Drop fixes with reported accuracy worse than m metres (default: keep all) |
| `--date <YYYY-MM-DD>` | Override the session date (only if it isn't in the folder path) |
| `--leap <n>` | GPS-UTC leap seconds for iTOW (default 18) |
| `--trim` | Trim leading/trailing standstill per session (off by default) |
| `--no-imu-csv` | Do not write the separate accel/gyro CSV (it is written by default) |
| `--imu-hz <n>` | Downsample the IMU to n Hz (default: native, about 1000 Hz on Insta360) |

Then in the Insta360 app: **Dashboard, Data Source, Local Files, Import**.

## Project layout

```
src/
  MyChron2Insta360.Core/   RaceStudioGpsCsv / RaceStudioImu / RaceStudioChannels (MyChron readers),
                           Insta360Trailer (native .insv/.lrv metadata reader),
                           ItowTime (GPS to UTC), Conversion (build/downsample),
                           GpxWriter, FitWriter, ImuCsvWriter
  MyChron2Insta360.App/    single .exe: CLI (Cli.cs) + WinForms GUI (MainForm.cs)
```

Notes: the original XRK file is a proprietary AiM binary, so this tool uses the documented CSV export
instead of a native XRK reader. The Insta360 trailer reader is a clean-room implementation of the
publicly documented format, with no external tool required; the layout was cross-checked against
[telemetry-parser](https://github.com/AdrianEddy/telemetry-parser) and [ExifTool](https://exiftool.org/),
and the output verified against ExifTool on real files.

## License

[GNU General Public License v3.0](LICENSE). Copyright (C) 2026 Bjoern Olausson.
