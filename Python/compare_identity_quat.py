#!/usr/bin/env python3
"""Identity-quaternion A/B 검증.

같은 세션의 검출에 대해 두 가지 ray 방향을 계산:
  1) 폰 자세 쿼터니언 적용 (현재 파이프라인)
  2) 쿼터니언 = identity (= 회전 미적용)
두 방향 간 각오차, 그리고 가상의 평탄 지면(camera 고도 기준)에 명중시킬 때의
수평 거리 차이를 정량화한다.

    python3 compare_identity_quat.py --session ../output/<세션>
    python3 compare_identity_quat.py --session ../output/<세션> --ground-alt 30

생성: <session>/projection/identity_quat_compare.csv (검출별 비교)
"""
import argparse
import csv
import math
import os
import sys
from pathlib import Path

import numpy as np

PROJECT_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(Path(__file__).resolve().parent))

from geo_projection import (
    DEFAULT_FOV_H_DEG, DEFAULT_HEIGHT, DEFAULT_WIDTH,
    R_phone_cam_landscape_left, R_phone_cam_landscape_right,
    intrinsics_from_fov, unity_ray_from_pixel,
)
from projection_pipeline import (
    find_csv, find_session, load_sensor_rows, resolve_detect_csv,
)


def ground_hit(origin, direction, ground_y=0.0):
    """y = ground_y 평면과 ray 교차점 반환 (Unity axes: Y=Up). 못 맞으면 None."""
    if abs(direction[1]) < 1e-9:
        return None
    t = (ground_y - origin[1]) / direction[1]
    if t < 0:
        return None
    return origin + t * direction


def main():
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--session", default=None,
                   help="세션 폴더 (기본: output/ 최신)")
    p.add_argument("--fov", type=float, default=DEFAULT_FOV_H_DEG)
    p.add_argument("--width", type=int, default=DEFAULT_WIDTH)
    p.add_argument("--height", type=int, default=DEFAULT_HEIGHT)
    p.add_argument("--orientation",
                   choices=["landscape-left", "landscape-right"],
                   default="landscape-left")
    p.add_argument("--detect-source",
                   choices=["live", "offline", "auto"], default="auto")
    p.add_argument("--ground-alt", type=float, default=30.0,
                   help="비교용 가상 카메라 고도(m). GPS alt 가 노이즈일 수 있어 고정값 사용 (기본 30).")
    args = p.parse_args()

    session = find_session(args.session)
    session_name = os.path.basename(session.rstrip(os.sep))
    print(f"[session] {session}")

    sensor_csv = find_csv(os.path.join(session, "sensor"), "sensor_data_")
    detect_csv = resolve_detect_csv(session, args.detect_source)
    sensors = load_sensor_rows(sensor_csv)
    if not sensors:
        raise SystemExit("Sensor CSV 에 유효 행이 없습니다.")
    print(f"[detect] {detect_csv}")
    print(f"[sensor] {sensor_csv}  ({len(sensors)} frames)")

    K = intrinsics_from_fov(args.width, args.height, args.fov)
    R_pc = (R_phone_cam_landscape_left() if args.orientation == "landscape-left"
            else R_phone_cam_landscape_right())
    IDENTITY = (0.0, 0.0, 0.0, 1.0)

    out_dir = os.path.join(session, "projection")
    os.makedirs(out_dir, exist_ok=True)
    out_csv = os.path.join(out_dir, "identity_quat_compare.csv")

    angles = []
    ground_diffs = []
    n_rows_out = 0
    skipped_no_sensor = 0

    with open(detect_csv, encoding="utf-8-sig") as fin, \
         open(out_csv, "w", newline="", encoding="utf-8-sig") as fout:
        reader = csv.DictReader(fin)
        writer = csv.writer(fout)
        writer.writerow([
            "frame_id", "class_name", "u", "v",
            "dir_w_x", "dir_w_y", "dir_w_z",
            "dir_id_x", "dir_id_y", "dir_id_z",
            "angle_deg",
            "hit_w_x", "hit_w_z",
            "hit_id_x", "hit_id_z",
            "ground_diff_m",
        ])
        for row in reader:
            try:
                fid = int(row["frame_id"])
                x1 = float(row["x1"]); x2 = float(row["x2"])
                y2 = float(row["y2"])
                cname = row.get("class_name", "")
            except (ValueError, KeyError):
                continue
            sensor = sensors.get(fid)
            if sensor is None:
                skipped_no_sensor += 1
                continue
            quat, lat, lng, alt = sensor
            u = 0.5 * (x1 + x2); v = y2

            # 1) 실제 쿼터니언
            _, dir_w = unity_ray_from_pixel(
                u, v, K, quat, R_pc,
                cam_lat=lat, cam_lng=lng, cam_alt=alt,
                origin_lat=lat, origin_lng=lng, origin_alt=alt,
            )
            # 2) identity 쿼터니언
            _, dir_i = unity_ray_from_pixel(
                u, v, K, IDENTITY, R_pc,
                cam_lat=lat, cam_lng=lng, cam_alt=alt,
                origin_lat=lat, origin_lng=lng, origin_alt=alt,
            )

            # 두 방향 사이 각 (단위벡터)
            dw = dir_w / max(np.linalg.norm(dir_w), 1e-12)
            di = dir_i / max(np.linalg.norm(dir_i), 1e-12)
            cosang = float(np.clip(np.dot(dw, di), -1.0, 1.0))
            ang_deg = math.degrees(math.acos(cosang))
            angles.append(ang_deg)

            # 평탄 지면 명중점 (가상 카메라 고도, ground y=0)
            origin_synth = np.array([0.0, args.ground_alt, 0.0])
            hw = ground_hit(origin_synth, dw)
            hi = ground_hit(origin_synth, di)
            gd = None
            if hw is not None and hi is not None:
                gd = math.sqrt((hw[0] - hi[0])**2 + (hw[2] - hi[2])**2)
                ground_diffs.append(gd)

            writer.writerow([
                fid, cname, f"{u:.2f}", f"{v:.2f}",
                f"{dw[0]:.6f}", f"{dw[1]:.6f}", f"{dw[2]:.6f}",
                f"{di[0]:.6f}", f"{di[1]:.6f}", f"{di[2]:.6f}",
                f"{ang_deg:.3f}",
                f"{hw[0]:.3f}" if hw is not None else "",
                f"{hw[2]:.3f}" if hw is not None else "",
                f"{hi[0]:.3f}" if hi is not None else "",
                f"{hi[2]:.3f}" if hi is not None else "",
                f"{gd:.3f}" if gd is not None else "",
            ])
            n_rows_out += 1

    print(f"\n[out] {out_csv}  ({n_rows_out} detections written, skipped {skipped_no_sensor} w/o sensor)")
    if angles:
        a = np.array(angles)
        print(f"\n=== 방향 차이 (각도) — {len(a)} 검출 ===")
        print(f"  mean={a.mean():.2f}°  median={np.median(a):.2f}°  std={a.std():.2f}°")
        print(f"  min={a.min():.2f}°   max={a.max():.2f}°")
        # 분포 히스토그램 (텍스트)
        bins = [0, 1, 5, 10, 30, 60, 90, 180]
        hist, _ = np.histogram(a, bins=bins)
        print(f"  분포 (각 구간, °):")
        for lo, hi, n in zip(bins[:-1], bins[1:], hist):
            bar = "#" * int(40 * n / max(hist.max(), 1))
            print(f"    [{lo:>3}, {hi:>3}) : {n:>5}  {bar}")
    if ground_diffs:
        g = np.array(ground_diffs)
        print(f"\n=== 가상 지면(고도 {args.ground_alt} m) 명중점 차이 (m) — {len(g)} 검출 ===")
        print(f"  mean={g.mean():.2f}m  median={np.median(g):.2f}m  std={g.std():.2f}m")
        print(f"  min={g.min():.2f}m   max={g.max():.2f}m")


if __name__ == "__main__":
    main()
