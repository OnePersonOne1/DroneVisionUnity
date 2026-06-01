"""
Offline join of detection CSV + sensor CSV → per-detection Unity-frame rays.

For each detection, takes the bbox bottom-center (u, v) = ((x1+x2)/2, y2),
looks up the matching sensor row by frame_id, and produces a ray in Unity
world coords. Ground intersection is left to Unity (Physics.Raycast against
the Incheon.fbx mesh) so the real terrain height is used.

Output: output/<session>/projection/
    projection_<session>.csv  (one row per detection)
    meta_<session>.json       (ENU origin + camera intrinsics + orientation)
"""

import argparse
import csv
import glob
import json
import os
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parent.parent

from geo_projection import (
    DEFAULT_FOV_H_DEG, DEFAULT_HEIGHT, DEFAULT_WIDTH,
    R_phone_cam_landscape_left, R_phone_cam_landscape_right,
    intrinsics_from_fov, unity_ray_from_pixel,
    unity_camera_axes_from_quat,
)

# 0-indexed column positions in sensor CSV (verified via header).
QUAT_IDX = 20       # qx, qy, qz, qw at 20..23
GPS_LAT_IDX = 25
GPS_LNG_IDX = 26
GPS_ALT_IDX = 27

OUT_SUBDIR = "projection"
OUT_HEADERS = [
    "frame_id", "sys_time", "class_id", "class_name", "confidence",
    "u", "v",
    "cam_lat", "cam_lng", "cam_alt",
    "dx", "dy", "dz",
    "cam_fx", "cam_fy", "cam_fz", "cam_ux", "cam_uy", "cam_uz",
]


def load_sensor_rows(sensor_csv):
    out = {}
    with open(sensor_csv, encoding="utf-8-sig") as f:
        reader = csv.reader(f)
        next(reader)
        for row in reader:
            if not row:
                continue
            try:
                fid = int(row[0])
                quat = (float(row[QUAT_IDX]),
                        float(row[QUAT_IDX + 1]),
                        float(row[QUAT_IDX + 2]),
                        float(row[QUAT_IDX + 3]))
                lat = float(row[GPS_LAT_IDX])
                lng = float(row[GPS_LNG_IDX])
                alt = float(row[GPS_ALT_IDX])
            except (ValueError, IndexError):
                continue
            out[fid] = (quat, lat, lng, alt)
    return out


def find_session(session_arg):
    if session_arg:
        return session_arg
    output_root = str(PROJECT_ROOT / "output")
    candidates = sorted(d for d in glob.glob(os.path.join(output_root, "*")) if os.path.isdir(d))
    if not candidates:
        raise SystemExit(f"No sessions under {output_root}")
    return candidates[-1]


def find_csv(dir_path, prefix):
    matches = sorted(glob.glob(os.path.join(dir_path, f"{prefix}*.csv")))
    if not matches:
        raise SystemExit(f"No CSV with prefix '{prefix}' in {dir_path}")
    return matches[0]


def resolve_detect_csv(session, mode):
    if mode == "live":
        return find_csv(os.path.join(session, "detect"), "detections_")
    if mode == "offline":
        return find_csv(os.path.join(session, "detect_offline"), "detections_")
    live_dir = os.path.join(session, "detect")
    if os.path.isdir(live_dir) and glob.glob(os.path.join(live_dir, "detections_*.csv")):
        return find_csv(live_dir, "detections_")
    return find_csv(os.path.join(session, "detect_offline"), "detections_")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--session", default=None,
                        help="Session folder (default: latest under output/)")
    parser.add_argument("--fov", type=float, default=DEFAULT_FOV_H_DEG)
    parser.add_argument("--width", type=int, default=DEFAULT_WIDTH)
    parser.add_argument("--height", type=int, default=DEFAULT_HEIGHT)
    parser.add_argument("--orientation",
                        choices=["landscape-left", "landscape-right"],
                        default="landscape-left")
    parser.add_argument("--origin", nargs=3, type=float,
                        metavar=("LAT", "LNG", "ALT"), default=None,
                        help="ENU origin (default: first GPS sample)")
    parser.add_argument("--classes", nargs="+", default=None,
                        help="Filter by class names, e.g. --classes human vehicle")
    parser.add_argument("--detect-source",
                        choices=["live", "offline", "auto"], default="auto")
    args = parser.parse_args()

    session = find_session(args.session)
    session_name = os.path.basename(session.rstrip(os.sep))
    print(f"[session] {session}")

    sensor_csv = find_csv(os.path.join(session, "sensor"), "sensor_data_")
    detect_csv = resolve_detect_csv(session, args.detect_source)
    print(f"[detect] {detect_csv}")
    print(f"[sensor] {sensor_csv}")

    sensors = load_sensor_rows(sensor_csv)
    if not sensors:
        raise SystemExit("Sensor CSV had no parseable rows.")

    if args.origin is None:
        first_fid = min(sensors.keys())
        _, lat0, lng0, alt0 = sensors[first_fid]
        print(f"[origin] first GPS sample @frame {first_fid}: "
              f"({lat0:.6f}, {lng0:.6f}, {alt0:.2f})")
    else:
        lat0, lng0, alt0 = args.origin
        print(f"[origin] user-specified: ({lat0:.6f}, {lng0:.6f}, {alt0:.2f})")

    K = intrinsics_from_fov(args.width, args.height, args.fov)
    R_pc = (R_phone_cam_landscape_left() if args.orientation == "landscape-left"
            else R_phone_cam_landscape_right())
    print(f"[intrinsics] fx={K[0,0]:.1f}, fy={K[1,1]:.1f}, "
          f"size={args.width}x{args.height}, fov_h={args.fov}°")
    print(f"[orientation] {args.orientation}")

    out_dir = os.path.join(session, OUT_SUBDIR)
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, f"projection_{session_name}.csv")
    meta_path = os.path.join(out_dir, f"meta_{session_name}.json")

    with open(meta_path, "w") as f:
        json.dump({
            "origin_lat": lat0,
            "origin_lng": lng0,
            "origin_alt": alt0,
            "fov_h_deg": args.fov,
            "width": args.width,
            "height": args.height,
            "orientation": args.orientation,
            "unity_axes": "X=East, Y=Up, Z=North",
        }, f, indent=2)
    print(f"[meta] {meta_path}")

    n_written = 0
    n_skipped_no_sensor = 0
    n_filtered = 0
    with open(detect_csv, encoding="utf-8-sig") as fin, \
         open(out_path, "w", newline="", encoding="utf-8-sig") as fout:
        reader = csv.DictReader(fin)
        writer = csv.writer(fout)
        writer.writerow(OUT_HEADERS)

        for row in reader:
            class_name = row["class_name"]
            if args.classes and class_name not in args.classes:
                n_filtered += 1
                continue
            try:
                fid = int(row["frame_id"])
                x1 = float(row["x1"]); x2 = float(row["x2"])
                y2 = float(row["y2"])
            except (ValueError, KeyError):
                continue

            sensor = sensors.get(fid)
            if sensor is None:
                n_skipped_no_sensor += 1
                continue
            quat, lat, lng, alt = sensor

            u = 0.5 * (x1 + x2)
            v = y2
            # Only the Unity-axis ray direction is needed downstream; the 인천
            # GPSEncoder receiver derives world position from raw camera GPS.
            _, dir_u = unity_ray_from_pixel(
                u, v, K, quat, R_pc,
                cam_lat=lat, cam_lng=lng, cam_alt=alt,
                origin_lat=lat0, origin_lng=lng0, origin_alt=alt0,
            )
            fwd_u, up_u = unity_camera_axes_from_quat(quat, R_pc)
            writer.writerow([
                fid, row["sys_time"], row["class_id"], class_name, row["confidence"],
                f"{u:.3f}", f"{v:.3f}",
                f"{lat:.8f}", f"{lng:.8f}", f"{alt:.3f}",
                f"{dir_u[0]:.6f}", f"{dir_u[1]:.6f}", f"{dir_u[2]:.6f}",
                f"{fwd_u[0]:.6f}", f"{fwd_u[1]:.6f}", f"{fwd_u[2]:.6f}",
                f"{up_u[0]:.6f}",  f"{up_u[1]:.6f}",  f"{up_u[2]:.6f}",
            ])
            n_written += 1

    print(f"[done] wrote {n_written} rows, "
          f"skipped {n_skipped_no_sensor} (no matching sensor), "
          f"filtered {n_filtered}")
    print(f"[output] {out_path}")


if __name__ == "__main__":
    main()
