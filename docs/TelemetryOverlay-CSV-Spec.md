# Producing a Telemetry Overlay CSV from a RaceStudio3 per-channel export

**Status:** working spec, empirically verified against Telemetry Overlay 2026-06-26 build
(`C:\Program Files\Telemetry Overlay\resources\app.asar`) and a real MyChron 6 session
(58 laps, Erzgebirgsring/Lichtenberg, 2026-08-02).

**Goal for the implementer:** add an option to `MyChron2Insta360` that merges the per-channel
`..._CSV` export folder into **one** CSV that
[Telemetry Overlay](https://goprotelemetryextractor.com/telemetry-overlay-gopro-gps) loads directly,
with all gauges bound.

Everything below marked **VERIFIED** was confirmed by reading Telemetry Overlay's own code/constants
or by cross-checking real telemetry. Items marked **INFERRED** are reasoned but not directly proven.

---

## 0. TL;DR for the agent

- Emit **one** wide CSV in **AiM "RS2Analysis" style** — a 13-line quoted preamble, a quoted header
  row, a quoted units row, then quoted data rows, CRLF throughout.
- Name every column so that `"<Header>" + " (" + "<units>" + ")"` matches an entry in Telemetry
  Overlay's built-in alias table (§4). Then **no `remapheaders.txt` is needed at all.**
- **Do not** write a `remapheaders.txt` that re-declares columns TO already recognises — that
  collision makes the whole file fail to load with *"Telemetry could not be found"* (§5).
- Watch three easy-to-miss details: the temperature unit must be **`°C`** not `C`; GPS `speed` in the
  source is **m/s** not km/h; and the raw IMU X axis points **backwards** (§7).
- **Do not hard-code the channel list.** §1/§4 describe one session's channels, not everything a
  MyChron can log. Discover the `*.csv` files present and look each one up in
  `docs/reference/telemetry-overlay-alias-table.txt` — see **§14**, which also covers channels with
  no alias (EGT, lambda) and when to omit a channel.

---

## 1. Input: the RaceStudio3 per-channel export

Folder `<session>_CSV\` containing one file per channel. Observed on MyChron 6:

| file | rate | header row | units row |
|---|---|---|---|
| `_GPS_o.csv` | 25 Hz | `time, itow, lat, lon, alt, speed, accuracy` | *(none)* |
| `_GPS.csv` | 10 Hz | same | *(none)* |
| `AccelerometerX/Y/Z.csv` | 50 Hz | `time, value` | `s, g` |
| `GyroX/Y/Z.csv` | 50 Hz | `time, value` | `s, deg/s` |
| `Steering Angle.csv` | 50 Hz | `time, value` | `s, deg` |
| `RPM.csv` | 20 Hz | `time, value` | `s, rpm` |
| `Internal Batt.csv` | 1 Hz | `time, value` | `s, V` |
| `Logger Temperature.csv` | 1 Hz | `time, value` | `s, C` |
| `_laps_and_splits.csv` | — | `run, lap, split, start, time` | `#, #, #, s, s` |

Notes:

- Use **`_GPS_o.csv`** (raw 25 Hz), not `_GPS.csv` (decimated). **VERIFIED**
- The channel files have **two** header lines (names, then units); `_GPS_o.csv` has **one**.
- All `time` columns share one base: seconds since session start. Channels start at slightly
  different offsets (0.000 / 0.001 / 0.094 / 0.109 / 0.112) — resample onto a common grid.
- **`speed` in `_GPS_o.csv` is m/s.** Verified by comparing against haversine ground speed over
  39 000 samples: ratio 0.9985. Multiply by 3.6 for km/h. **VERIFIED**

### Time base / absolute clock

The converter's own `<session>_imu.csv` maps elapsed seconds to UTC in its first row, e.g.
`2026-08-02T16:22:00.920Z, 0, …`. That anchors the whole session:

```
local = utc0 + t          (Europe/Berlin = UTC+2 in summer)
```

Cross-check the far end of the session against the last `_imu.csv` row before trusting it.
In the reference session this agreed to 12 ms across 57 minutes. **VERIFIED**

### A `.vbo` as input

A Dragy (or other Racelogic VBOX) `.vbo` log goes through the same writer with GPS alone: no IMU, no
channel files, no laps. Its `time` column is the UTC time of day, and seconds from the first fix are
the time base, as for a MyChron session. It adds two streams a MyChron does not record, the course
over ground and the satellite count, which get the `GPS Heading` and `satellites` columns of §4. A
MyChron session has neither column. In both cases, rows without a position fix (0, 0) stay out of the
GPS columns, as they stay out of the GPX and FIT.

---

## 2. Telemetry Overlay accepts two formats

| | AiM RS2Analysis | Custom CSV |
|---|---|---|
| preamble | required (13 lines) | none |
| units | separate 2nd row | in header, `name (unit)` |
| header matching | alias table (§4) | fixed stream names (§9) |
| remapping | `remapheaders.txt` | not supported |
| **recommendation** | **use this** | fallback |

The AiM path is preferred: it supports far more channels (steering, battery, temperature, gyro,
lap beacons) than the Custom CSV stream list.

---

## 3. AiM RS2Analysis file structure — exact

Reverse-engineered from a genuine RaceStudio3 export and confirmed against the parser's regexes.

```
"Format","AiM CSV File"                                    <- line 1
"Session","<track>"
"Vehicle",""
"Racer","<driver>"
"Championship",""
"Comment",""
"Date","Sunday, August 2, 2026"                            <- "%A, %B %-d, %Y"
"Time","6:22 PM"                                           <- "%-I:%M %p"
"Sample Rate","50"
"Duration","3427"
"Segment","Session"
"Beacon Markers","61.805","122.239",...                    <- cumulative lap-end times, seconds
"Segment Times","1:01.805","1:00.434",...                  <- ignored by TO, see §8
                                                           <- BLANK line
"Time","GPS Speed","GPS Latitude",...                      <- header row
"s","km/h","deg",...                                       <- units row
                                                           <- BLANK line
"0.000","6.4944","50.83924070",...                         <- data rows
```

Hard requirements — **every field is double-quoted, including numeric data**, and line endings are
**CRLF**. Encoding: ASCII, except the `°` in the temperature unit (§6).

### Parser detection rules **VERIFIED**

Extracted verbatim from the V8 constant pool of `resources/app.asar` → `main.jsc`:

```
"Sample Rate","((\d|\.)+?)"
"Beacon Markers","([\d.]+)"
"Beacon Markers","(.+)"
^"Time"                 <- locates the HEADER row
^"s(ec)?"               <- the row after it must match, locating the UNITS row
^"?\d                   <- locates the first DATA row
^"(.*?)"$
```

Consequences:

- The header row's first cell **must** be exactly `"Time"`, and the next line's first cell must be
  `"s"` or `"sec"`. The preamble's own `"Time","6:22 PM"` line is harmless because the line after it
  (`"Sample Rate"`) fails `^"s(ec)?"` — the parser keeps scanning.
- The literal string `"AiM CSV File"` does **not** appear in the binary, so it is not the format
  signature. Keep the preamble anyway — the `Sample Rate` / `Beacon Markers` regexes read it.

The parser also carries repair hacks for known-broken AiM exports (do not emit these):
it rewrites `"GPS Longitude""Logger Temperature"` → `"GPS Longitude","Logger Temperature"`,
`"deg""` → `"deg","`, and fixes the misspelling `Latittude`.

---

## 4. The alias table — the thing that actually matters

Telemetry Overlay joins the header cell and units cell into `Header (units)` and looks that string up
in a fixed table. **Match it exactly and everything binds with no remapping.**

Recommended column set, all **VERIFIED** present in the table:

| header | units | resolves to | notes |
|---|---|---|---|
| `Time` | `s` | `Time (s)` | also `Time (sec)`, `Time (ms)` |
| `GPS Speed` | `km/h` | `GPS Speed (SPEEDUNITS)` | placeholder — any speed unit works |
| `GPS Latitude` | `deg` | `GPS Latitude (deg)` | |
| `GPS Longitude` | `deg` | `GPS Longitude (deg)` | |
| `GPS Altitude` | `m` | `GPS Altitude (ALTITUDEUNITS)` | prefer `m` |
| `GPS Heading` | `deg` | `GPS Heading (deg)` | `.vbo` only; resampled the short way round 0/360 |
| `satellites` | `sats` | `satellites (sats)` | `.vbo` only |
| `InlineAcc` | `g` | `InlineAcc (g)` | AiM's name for **longitudinal** |
| `LateralAcc` | `g` | `LateralAcc (g)` | |
| `VerticalAcc` | `g` | `VerticalAcc (g)` | |
| `RollRate` | `deg/s` | `RollRate (deg/s)` | |
| `PitchRate` | `deg/s` | `PitchRate (deg/s)` | |
| `YawRate` | `deg/s` | `YawRate (deg/s)` | |
| `Steering Angle` | `deg` | `Steering Angle (deg)` | |
| `Battery` | `V` | `Battery (V)` | **not** `Internal Batt` |
| `Logger Temperature` | `°C` | `Logger Temperature (°C)` | **see §6** |

Other accepted spellings in the table, if you prefer to pass raw sensor axes:
`AccelerometerX/Y/Z (g)`, `GyroX/Y/Z (deg/s)`, `GPS Nsat ( )`, `GPS PosAccuracy (m)`,
`GPS SpdAccuracy (m/s)`, `Steering_Angle (deg)`, `STEER ANGLE (deg)`, `LoggerTemp (°C)`.

### Names that do **not** work

`LongAcc (g)` (the table has `LongAcc[g]` with square brackets), `LatAcc (g)`, `VertAcc (g)`
(absent entirely), `Internal Batt (V)`. Using these was the original cause of a failed load.

---

## 5. `remapheaders.txt` — usually unnecessary

Syntax, one mapping per line:

```
[stream identifier]=[header name in CSV]=[SI units]
```

The 19 valid identifiers, **VERIFIED** as real strings in `main.jsc`:

```
EngineRPM  Gear  Throttle%  Brake%  BrakePressure
LateralAcceleration  LongitudinalAcceleration  VerticalAcceleration
Fuel%  BatteryVolts
GyroscopeLateralAxis  GyroscopeLongitudinalAxis  GyroscopeVerticalAxis
SteeringAngle  Temperature
GPSLatitude  GPSLongitude  Altitude  Speed
```

> ### ⚠ The trap
>
> **Do not emit a `remapheaders.txt` for columns the alias table already covers.** Re-declaring
> already-recognised headers makes the entire file fail to load with the unhelpful message
> *"Telemetry could not be found"* — no per-column diagnostic. This cost a long debugging session.
>
> With the §4 column set, **write no `remapheaders.txt` at all.**

Emit one **only** for channels whose `Header (units)` is genuinely absent from the alias table —
an aftermarket brake-pressure sensor, lambda, EGT — and then include lines *only* for those.

Unmapped columns are still usable: the user enables **"Read extra streams"** and binds a Custom
gauge. `Lap` is such a column (there is no `Lap (...)` alias of any unit).

---

## 6. Units gotchas

- **`Logger Temperature` must use `°C`, not `C`.** The alias key is byte-exactly
  `Logger Temperature (\xB0C)`. With plain `C` the column silently becomes an unmapped extra stream —
  no error, no warning, and the Thermometer gauge stays greyed out. **VERIFIED** (byte-level)
  Write the file UTF-8; if TO shows a mangled `Â°C`, re-emit that byte as Windows-1252 `0xB0`.
- `SPEEDUNITS` / `ALTITUDEUNITS` are placeholders substituted with whatever unit the file declares,
  so speed and altitude units are permissive. Prefer `km/h` and `m`.
- Accel `g`, gyro `deg/s`, steering `deg`, battery `V`, lat/lon `deg` all match exactly.

---

## 7. Sensor frame and channel math

### The raw MyChron 6 IMU frame is X = backward, Y = right, Z = up **VERIFIED**

Derived from the data, then independently confirmed: that frame *predicts* `GyroZ` positive = left
turn, and measuring `GyroZ` against GPS heading rate gives r = −0.75. It is a right-handed frame.

Do not assume it — a differently-mounted logger may differ. Re-derive with §10.

### Conversion to AiM's semantic channels

```
InlineAcc   = -AccelerometerX      (+ = accelerating forward)
LateralAcc  = +AccelerometerY      (+ = right-hand corner)
VerticalAcc = +AccelerometerZ      (+ = up, ~+1 g at rest)
RollRate    = -GyroX               (+ = rolling right)
PitchRate   = +GyroY               (+ = nose up)
YawRate     = -GyroZ               (+ = yawing right)
Steering Angle = as logged         (+ = steering right, r = +0.79 vs GPS heading rate)
```

The accel/lateral/yaw/steering signs are directly measured. Roll and pitch follow from the validated
frame geometry rather than direct measurement — on a rigid kart chassis the empirical check is
inconclusive (r ≈ 0.06 / 0.23) because roll rate is dominated by kerb strikes. **INFERRED**

### Mount tilt

The reference logger sat at ~13° backward tilt, leaving `InlineAcc` resting at −0.246 g instead of
zero (gravity bleeding onto the longitudinal axis). Consider exposing an optional static tilt
correction derived from the session-mean gravity vector. Not applied by default — it transforms
the user's raw data.

---

## 8. Laps and beacons

- **`"Beacon Markers"`** is consumed: quoted, comma-separated decimal seconds on the same time base
  as the `Time` column, one per lap end (`start + duration` from `_laps_and_splits.csv`).
  This is what drives TO's Lap Timer / Sector Times. **VERIFIED**
- **`"Segment Times"` is ignored** — that string does not exist anywhere in the binary. Harmless to
  emit. **VERIFIED**
- **Lap numbering differs between tools.** `_laps_and_splits.csv` counts the out-lap as lap 1;
  RaceStudio3 labels it `out` and starts at 1 on the next lap. So:

  ```
  RaceStudio lap N  ==  _laps_and_splits lap N+1  ==  Telemetry Overlay lap N+1
  ```

  TO derives its counter from the beacon markers, so it matches the converter, not RaceStudio.
  If you emit a `Lap` column, consider RaceStudio numbering (out-lap = 0) and document it.

---

## 9. Format B: Custom CSV (fallback)

Single header row, **no units row, no preamble**, units in parentheses:

```
time (s),lat (deg),lon (deg),alt (m),speed (m/s),accel x (m/s²),accel y (m/s²),accel z (m/s²),gyro x (deg/s),gyro y (deg/s),gyro z (deg/s),lap
```

Time alternatives: `utc (ms)`, `date` (`YYYY-MM-DDTHH:mm:ss.sssZ`), `time (ms)`, `timecode`.

> **Axis convention differs from intuition.** TO maps
> `'right/left' → accelerometer-x`, `'forward/back' → accelerometer-y`, `'up/down' → accelerometer-z`.
> So **`accel x` is LATERAL and `accel y` is LONGITUDINAL** — the opposite of the obvious guess.
> **VERIFIED** Accelerations are in **m/s²** here, not g; speed in **m/s**, not km/h.

There is no Custom CSV stream for steering, battery or temperature — they become extra streams only.
This is why the AiM format is preferred.

---

## 10. Validation recipes

Cheap checks worth building into tests — all use only GPS as ground truth:

1. **Bearing convention.** `bearing()` with 0 = N increasing clockwise. Net heading change over a
   full lap must be **±360°**; negative = counter-clockwise circuit. If this comes out inverted,
   every downstream sign conclusion inverts with it.
2. **Speed units.** `median(speed_column / haversine_ground_speed)` → 1.0 means m/s, 3.6 means km/h.
3. **Which axis is longitudinal.** Correlate each accel axis against `d(GPS speed)/dt`. Winner is
   longitudinal; the sign of r tells you whether to negate.
4. **Which axis is lateral.** Correlate against `v · yaw_rate`. In the reference session
   `LateralAcc` reached r = +0.92.
5. **Which axis is vertical.** Session mean ≈ ±1 g.
6. **Yaw sign.** Correlate the candidate gyro axis against GPS heading rate (r = −0.75 raw here).

---

## 11. Known data pitfalls

- **RPM can be entirely zero.** In the reference session all 68 477 samples were exactly `0.000`
  (no pickup connected). Detect all-zero channels and omit them rather than emitting a dead gauge.
- `_GPS.csv` is a decimation of `_GPS_o.csv` — always prefer the latter.
- Channels have different start offsets and rates; interpolate onto a common grid (50 Hz suits the
  native accel/gyro/steering rate) and clamp rather than extrapolate at the edges.
- A 57-minute session at 50 Hz is ~171 000 rows / ~22 MB. Telemetry Overlay handles this fine.

---

## 12. Sign conventions are user-preference, not correctness

Every gauge in Telemetry Overlay that can be mirrored exposes an **"invert"** checkbox whose entire
implementation is `value *= -1` (`p5/draws/compass.js:237`). The whole CSV → gauge chain is odd and
linear, so **writing `-x` is pixel-identical to writing `+x` with invert ticked.** **VERIFIED**

Two consequences:

1. Any sign choice you bake in is reversible by the user, and vice versa.
2. You cannot determine the "right" sign from TO's source alone for the accelerometer — the importer
   that sits between the CSV and the renderer is compiled V8 bytecode, and the readable renderer code
   is consistent with two opposite scenarios. Resolve empirically, or expose a flag.

Observed in practice for this build:

- **Steering** renders mirrored, so it needs negating (or invert ticked). **VERIFIED** empirically.
- **Lateral acceleration** renders in the "seat of the pants" (centrifugal/outward) sense with the
  §7 convention. Some users prefer that; do not silently flip it. Make it an option.

Suggested CLI surface:

```
--telemetry-overlay-csv              emit the merged AiM CSV
--to-rate <hz>                       output grid rate (default 50)
--to-invert-steering                 negate steering (default ON: matches TO's gauge)
--to-invert-lateral                  negate lateral/G-force (default OFF: seat-of-the-pants)
--to-tilt-correct                    remove static mount tilt (default OFF)
```

---

## 13. Reference implementation

A working Python implementation that produces a verified-loading file lives beside this document:

```
docs/reference/build_telemetry_overlay_csv.py
```

Point `BASE` at a `..._CSV` folder and run it. It emits the AiM CSV, a 60-second probe file for fast
load tests, and the Custom CSV fallback. Treat it as executable documentation of §3–§9, not as
production code — it is single-purpose and has no error handling.

---

## 14. Generalising to arbitrary MyChron channels

> **The channel list in §1 and §4 is not exhaustive.** It is what one particular session happened to
> log: GPS, 3-axis accel, 3-axis gyro, steering, RPM, internal battery, logger temperature. A MyChron
> with probes fitted also emits water/head temperature, EGT, lambda, brake pressure, throttle and
> more, and an expansion module adds arbitrary user-named channels. **Do not hard-code the channel
> list.**

### Design: discover, then look up

```
for each *.csv in <session>_CSV\
    skip _GPS.csv, _GPS_o.csv, _laps_and_splits.csv, *_imu.csv     (handled separately)
    read line 1 -> channel name, line 2 -> units
    key = $"{name} ({units})"

    if key matches the alias table            -> emit with that exact name+unit
    else if a documented rename exists        -> emit under the renamed header (table below)
    else                                      -> emit verbatim as an EXTRA stream
                                                 (optionally add a remapheaders.txt line, §5)
```

The complete alias table is checked into this repo:

```
docs/reference/telemetry-overlay-alias-table.txt          (483 entries)
```

Look strings up in it. **Never derive a unit string** — the table is internally inconsistent. For
example `Logger Temperature (°C)` requires the degree sign but `Coolant Temperature (C)` requires a
plain `C`, and `Internal Battery (V)` exists while `Internal Batt (V)` does not.

### Recommended renames for common MyChron channels

RaceStudio's channel names often differ from the alias table by a word. These map cleanly:

| MyChron / RaceStudio channel | emit as header | units | notes |
|---|---|---|---|
| `Internal Batt` | `Battery` | `V` | `Internal Battery` also works |
| `Logger Temperature` | `Logger Temperature` | `°C` | degree sign required |
| `Water Temp`, `Water Temperature` | `Coolant Temperature` | `C` | **plain `C`**, no degree sign |
| `RPM` | `RPM` | `rpm` | binds as-is |
| `Speed`, wheel speed | `Speed` | `km/h` | |
| `Brake Press`, brake pressure | `Brake Pressure` | `bar` | |
| `Throttle`, TPS | `Throttle Position` | `%` | |
| `Gear` | `Gear` | `#` | `(gear)` also accepted |
| `Oil Temp` | `Oil Temperature` | `°C` | degree sign here |
| `Oil Press` | `Oil Pressure` | `bar` | |
| `AccelerometerX/Y/Z` | see §7 | `g` | re-express, don't pass raw |

### Channels with no alias at all **VERIFIED**

Searched the full table: **EGT / exhaust gas temperature, lambda, AFR, head temperature and CHT have
no entry**. These are common MyChron add-ons, so handle them deliberately:

- **Preferred:** emit verbatim and let them arrive as extra streams. The user enables
  *"Read extra streams"* and binds a Custom gauge. Nothing breaks, the data is present, and there is
  no risk of the §5 collision.
- **Optional:** map *one* temperature-ish channel through `remapheaders.txt` using the
  `Temperature` stream identifier, if you want it on the built-in Temperature gauge. Only one channel
  can claim it. Do not do this by default.

### When to omit a channel

Skip a channel only when it carries no information:

- **All-zero** — the strong signal of a sensor that is not connected. In the reference session all
  68 477 `RPM.csv` samples were exactly `0.000` because no pickup was fitted; emitting it produces a
  permanently dead tachometer. Skip by default.
- **Constant, non-zero** — warn but keep. A flat value may be legitimate (a battery at 4.1 V for a
  whole session, an unused-but-valid input). Do not silently drop it.

Never skip a channel merely because it is unrecognised — unrecognised is not the same as empty, and
extra streams are a supported destination.

Log every decision (`bound as X`, `extra stream`, `skipped: all zero`) so a user can tell why an
expected gauge did not appear.

---

## 15. Provenance

Facts marked VERIFIED came from one of:

- **Original app sources.** `resources/app.asar` embeds `renderer.js.map`, a 26 MB source map
  containing 844 un-minified original sources. Extract `sourcesContent` to recover
  `src/renderer/p5/Gauges/*.js`, `p5/draws/compass.js`, `p5/visual/*.js` etc.
- **String constants.** The main-process parser is compiled V8 bytecode (`main.jsc`); its logic is
  unreadable but its string pool yields the alias table, the detection regexes and the remap
  identifiers. Scan for printable runs, and note that Latin-1 bytes such as `0xB0` (`°`) are
  invisible to an ASCII-only scan — that is how the `°C` requirement was initially missed.
- **Cross-checking real telemetry** against GPS-derived ground truth (§10), and against RaceStudio3's
  own reported lap times and speeds.
