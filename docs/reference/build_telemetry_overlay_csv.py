import csv, math, os, datetime

BASE = r"C:\AIM_SPORT\RaceStudio3\user\data\2026-08-02\Bjoern\68\55 Runden Rennen\Lichtenberg\Race\Custom Text 1\Custom Text 2\Bjoern_68_Lichtenberg_Race_35004726_a_0199.xrz_SN35004726_CSV"
OUT_AIM    = os.path.join(BASE, "MyChron6_TelemetryOverlay.csv")
OUT_CUSTOM = os.path.join(BASE, "MyChron6_CustomCSV.csv")
OUT_TEST   = os.path.join(BASE, "MyChron6_TEST_60s.csv")
OUT_MAP    = os.path.join(BASE, "remapheaders.txt")

def load2(name):
    t, v = [], []
    with open(os.path.join(BASE, name), newline="") as f:
        r = csv.reader(f); next(r); next(r)
        for row in r:
            if len(row) < 2 or not row[0].strip(): continue
            try: t.append(float(row[0])); v.append(float(row[1]))
            except ValueError: pass
    return t, v

def interp(t, v, grid):
    out, j, n = [], 0, len(t)
    for x in grid:
        if x <= t[0]: out.append(v[0]); continue
        if x >= t[-1]: out.append(v[-1]); continue
        while j + 1 < n and t[j+1] < x: j += 1
        while j > 0 and t[j] > x: j -= 1
        t0, t1 = t[j], t[j+1]
        f = (x - t0) / (t1 - t0) if t1 > t0 else 0.0
        out.append(v[j] + f * (v[j+1] - v[j]))
    return out

gt, glat, glon, galt, gspd = [], [], [], [], []
with open(os.path.join(BASE, "_GPS_o.csv"), newline="") as f:
    r = csv.reader(f); next(r)
    for row in r:
        if len(row) < 7: continue
        try:
            gt.append(float(row[0])); glat.append(float(row[2])); glon.append(float(row[3]))
            galt.append(float(row[4])); gspd.append(float(row[5]))
        except ValueError: pass

ax_t, ax = load2("AccelerometerX.csv"); ay_t, ay = load2("AccelerometerY.csv")
az_t, az = load2("AccelerometerZ.csv"); gx_t, gx = load2("GyroX.csv")
gy_t, gy = load2("GyroY.csv");          gz_t, gz = load2("GyroZ.csv")
st_t, st = load2("Steering Angle.csv"); bt_t, bt = load2("Internal Batt.csv")
tp_t, tp = load2("Logger Temperature.csv")

laps = []
with open(os.path.join(BASE, "_laps_and_splits.csv"), newline="") as f:
    r = csv.reader(f); next(r); next(r)
    for row in r:
        if len(row) < 5: continue
        try: laps.append((int(row[1]), float(row[3]), float(row[4])))
        except ValueError: pass

DT = 0.02
N = int(ax_t[-1] / DT) + 1
grid = [round(i * DT, 3) for i in range(N)]

LAT = interp(gt, glat, grid); LON = interp(gt, glon, grid); ALT = interp(gt, galt, grid)
SPD_MS = interp(gt, gspd, grid)
AX = interp(ax_t, ax, grid); AY = interp(ay_t, ay, grid); AZ = interp(az_t, az, grid)
GX = interp(gx_t, gx, grid); GY = interp(gy_t, gy, grid); GZ = interp(gz_t, gz, grid)
ST = interp(st_t, st, grid); BT = interp(bt_t, bt, grid); TP = interp(tp_t, tp, grid)

# sign normalisation (raw frame X=backward, Y=right, Z=up -> driver-intuitive frame)
LONGACC   = [-v for v in AX]
LATACC    = [ v for v in AY]
VERTACC   = [ v for v in AZ]
ROLLRATE  = [-v for v in GX]
PITCHRATE = [ v for v in GY]
YAWRATE   = [-v for v in GZ]
SPD_KMH   = [v * 3.6 for v in SPD_MS]

# Steering: MyChron logs + = steering RIGHT (verified r=+0.79 vs GPS heading rate), but
# Telemetry Overlay's Steering Wheel gauge renders that mirrored. This file targets TO, so
# negate here and leave the gauge's "invert" box UNTICKED. The box is exactly
# `value *= -1` (draws/compass.js:237), so the two are pixel-identical.
#
# The six IMU channels below are deliberately NOT flipped: TO then shows lateral g in the
# "seat of the pants" (centrifugal) sense, which is the preferred default.
ST = [-v for v in ST]

LAP = [0]*N
for lapno, start, dur in laps:
    for i in range(max(0, int(start/DT)), min(N, int((start+dur)/DT)+1)):
        LAP[i] = lapno - 1        # RaceStudio numbering: out-lap = 0, then 1..57

# ---------------- AiM RS2Analysis envelope ----------------
session_dt = datetime.datetime(2026, 8, 2, 18, 22, 0)          # 16:22:00.920Z -> 18:22 CEST
date_str = session_dt.strftime("%A, %B ") + str(session_dt.day) + session_dt.strftime(", %Y")
time_str = session_dt.strftime("%I:%M %p").lstrip("0")
markers = [f"{start+dur:g}" for _, start, dur in laps]

def seg(t):
    m = int(t // 60); s = t - 60*m
    return f"{m}:{s:06.3f}"
seg_times = [seg(dur) for _, _, dur in laps]

def q(vals): return ",".join('"' + str(v) + '"' for v in vals)

# every header/unit pair below resolves to a name in Telemetry Overlay's built-in
# AiM alias table (verified in main.jsc), so no remapheaders.txt is required:
#   Time (s)  GPS Speed (SPEEDUNITS)  GPS Latitude (deg)  GPS Longitude (deg)
#   GPS Altitude (ALTITUDEUNITS)  InlineAcc (g)  LateralAcc (g)  VerticalAcc (g)
#   RollRate (deg/s)  PitchRate (deg/s)  YawRate (deg/s)  Steering Angle (deg)
#   Battery (V)  Logger Temperature (C)      [Lap is an unmapped extra stream]
HDR = ["Time","GPS Speed","GPS Latitude","GPS Longitude","GPS Altitude",
       "InlineAcc","LateralAcc","VerticalAcc","RollRate","PitchRate","YawRate",
       "Steering Angle","Battery","Logger Temperature","Lap"]
# NB: the temperature alias key in main.jsc is byte-exactly "Logger Temperature (\xb0C)" --
# a plain "C" matches nothing and leaves the column as an unmapped extra stream.
UNI = ["s","km/h","deg","deg","m","g","g","g","deg/s","deg/s","deg/s","deg","V","°C","#"]

def write_aim(path, nrows):
  with open(path, "w", newline="", encoding="utf-8") as f:
    w = f.write
    w(q(["Format","AiM CSV File"]) + "\r\n")
    w(q(["Session","Lichtenberg"]) + "\r\n")
    w(q(["Vehicle",""]) + "\r\n")
    w(q(["Racer","Bjoern"]) + "\r\n")
    w(q(["Championship","55 Runden Rennen"]) + "\r\n")
    w(q(["Comment",""]) + "\r\n")
    w(q(["Date", date_str]) + "\r\n")
    w(q(["Time", time_str]) + "\r\n")
    w(q(["Sample Rate","50"]) + "\r\n")
    w(q(["Duration", str(int(grid[-1]))]) + "\r\n")
    w(q(["Segment","Session"]) + "\r\n")
    w(q(["Beacon Markers"] + markers) + "\r\n")
    w(q(["Segment Times"] + seg_times) + "\r\n")
    w("\r\n")
    w(q(HDR) + "\r\n")
    w(q(UNI) + "\r\n")
    w("\r\n")
    for i in range(nrows):
        w(q([f"{grid[i]:.3f}", f"{SPD_KMH[i]:.4f}", f"{LAT[i]:.8f}", f"{LON[i]:.8f}",
             f"{ALT[i]:.4f}", f"{LONGACC[i]:.2f}", f"{LATACC[i]:.2f}", f"{VERTACC[i]:.2f}",
             f"{ROLLRATE[i]:.1f}", f"{PITCHRATE[i]:.1f}", f"{YAWRATE[i]:.1f}",
             f"{ST[i]:.1f}", f"{BT[i]:.1f}", f"{TP[i]:.1f}", str(LAP[i])]) + "\r\n")

# ---------------- Custom CSV fallback (single header row, no units row) ----------------
G = 9.80665
CHDR = ("time (s),lat (deg),lon (deg),alt (m),speed (m/s),"
        "accel x (m/s\u00b2),accel y (m/s\u00b2),accel z (m/s\u00b2),"
        "gyro x (deg/s),gyro y (deg/s),gyro z (deg/s),"
        "steering angle (deg),battery (V),logger temperature (\u00b0C),lap")
with open(OUT_CUSTOM, "w", newline="", encoding="utf-8") as f:
    w = f.write
    w(CHDR + "\n")
    for i in range(N):
        # TO axis convention (verified in app.asar):
        #   'right/left' -> accelerometer-x, 'forward/back' -> accelerometer-y, 'up/down' -> accelerometer-z
        # so x = lateral, y = longitudinal, z = vertical; gyro follows the same body frame
        # (x=right -> pitch, y=forward -> roll, z=up -> yaw).
        w(f"{grid[i]:.3f},{LAT[i]:.8f},{LON[i]:.8f},{ALT[i]:.2f},{SPD_MS[i]:.3f},"
          f"{LATACC[i]*G:.3f},{LONGACC[i]*G:.3f},{VERTACC[i]*G:.3f},"
          f"{PITCHRATE[i]:.1f},{ROLLRATE[i]:.1f},{YAWRATE[i]:.1f},"
          f"{ST[i]:.1f},{BT[i]:.1f},{TP[i]:.1f},{LAP[i]}\n")

write_aim(OUT_AIM, N)
write_aim(OUT_TEST, min(N, 3000))          # 60 s probe file for a fast load test

# remapheaders.txt is no longer needed: every column now matches a built-in alias.
# Leaving it in place would re-declare headers TO already knows, so move it aside.
if os.path.exists(OUT_MAP):
    os.replace(OUT_MAP, OUT_MAP + ".disabled")

print(f"Date  : {date_str}   Time: {time_str}")
print(f"laps  : {len(laps)}  first marker {markers[0]}  last marker {markers[-1]}")
print(f"AiM   : {os.path.basename(OUT_AIM)}  ({os.path.getsize(OUT_AIM)/1e6:.1f} MB, {N} rows)")
print(f"Probe : {os.path.basename(OUT_TEST)} ({os.path.getsize(OUT_TEST)/1e6:.2f} MB, 3000 rows)")
print(f"Custom: {os.path.basename(OUT_CUSTOM)}  ({os.path.getsize(OUT_CUSTOM)/1e6:.1f} MB)")
print(f"remapheaders.txt -> disabled (no longer required)")
