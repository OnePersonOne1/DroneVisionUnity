"""Replay a saved projection_<session>.csv as UDP packets — same wire format
as live mode, so Unity's UDP receiver sees identical traffic.

기본 모드 (안전·기존 호환):
  projection CSV 에 들어 있는 프레임(=검출 1개 이상이었던 프레임)만 송신.

--realtime 모드 (신규):
  sensor CSV 를 함께 읽어 **캡처된 모든 frame_id** 를 원본 sys_time 간격으로 송신.
  검출 0개 프레임은 detections=[] 의 빈 패킷이 나가서 Unity 가 실제 캡처
  타임라인(검출 공백 포함)을 그대로 보게 된다. 라이브의 실시간 흐름을 재현하려는
  용도(특히 onlive 흉내). cam_forward / cam_up 은 IMU quat → 추정.

Examples:
    python3 replay_offline.py                              # latest session, projection-only, sys_time pacing
    python3 replay_offline.py --rate 10                    # fixed 10 Hz
    python3 replay_offline.py --realtime                   # 모든 캡처 프레임(빈 detect 포함), sys_time 간격
    python3 replay_offline.py --host 192.168.0.42 --loop   # 다른 머신으로, 무한 반복
"""

import argparse
import csv
import glob
import os
import time
from collections import defaultdict
from pathlib import Path

from udp_sender import UdpSender
from geo_projection import (
    R_phone_cam_landscape_left, R_phone_cam_landscape_right,
    unity_camera_axes_from_quat,
)

PROJECT_ROOT = Path(__file__).resolve().parent.parent

# sensor CSV 열 인덱스 — IP_webcam.py 의 final_headers 와 일치해야 함.
# ['frame_id','img_filename','sys_time','sensor_time', + 21개 SENSOR_HEADERS + 4개 GPS_HEADERS]
# 즉 quat(x*sin, y*sin, z*sin, cos)는 4+16=20..23, GPS lat/lng/alt 는 4+21..23 = 25..27.
SENSOR_FID_IDX = 0
SENSOR_SYS_TIME_IDX = 2
SENSOR_QUAT_IDX = 20
SENSOR_GPS_LAT_IDX = 25
SENSOR_GPS_LNG_IDX = 26
SENSOR_GPS_ALT_IDX = 27


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


def load_projection(csv_path):
    frames = defaultdict(list)
    frame_time = {}
    frame_pose = {}   # fid -> (cam_forward[3], cam_up[3])
    with open(csv_path, encoding="utf-8-sig") as f:
        for row in csv.DictReader(f):
            fid = int(row["frame_id"])
            frame_time.setdefault(fid, float(row["sys_time"]))
            if fid not in frame_pose and "cam_fx" in row:
                try:
                    frame_pose[fid] = (
                        [float(row["cam_fx"]), float(row["cam_fy"]), float(row["cam_fz"])],
                        [float(row["cam_ux"]), float(row["cam_uy"]), float(row["cam_uz"])],
                    )
                except (KeyError, ValueError):
                    pass
            frames[fid].append({
                "class_name": row["class_name"],
                "class_id": int(row["class_id"]),
                "confidence": float(row["confidence"]),
                "u": float(row["u"]), "v": float(row["v"]),
                "cam_lat": float(row["cam_lat"]),
                "cam_lng": float(row["cam_lng"]),
                "cam_alt": float(row["cam_alt"]),
                "direction": [float(row["dx"]), float(row["dy"]), float(row["dz"])],
            })
    return frames, frame_time, frame_pose


def load_sensor_index(sensor_csv):
    """sensor CSV → fid : (sys_time, quat_xyzw)  — realtime 모드 전용.

    realtime 모드에서는 검출 0개 프레임의 카메라 자세를 IMU quat 으로 추정해 패킷에
    cam_forward/cam_up 만 실어 보낸다(detections=[]). 검출 있는 프레임의 fwd/up 은
    projection CSV 가 우선이므로 quat 만 있으면 충분."""
    out = {}
    with open(sensor_csv, encoding="utf-8-sig") as f:
        reader = csv.reader(f)
        try:
            next(reader)
        except StopIteration:
            return out
        for row in reader:
            if not row:
                continue
            try:
                fid = int(row[SENSOR_FID_IDX])
                sys_time = float(row[SENSOR_SYS_TIME_IDX])
                quat = (float(row[SENSOR_QUAT_IDX]),
                        float(row[SENSOR_QUAT_IDX + 1]),
                        float(row[SENSOR_QUAT_IDX + 2]),
                        float(row[SENSOR_QUAT_IDX + 3]))
            except (ValueError, IndexError):
                continue
            out[fid] = (sys_time, quat)
    return out


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--session", default=None,
                   help="Session folder (default: latest under output/)")
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=9870)
    p.add_argument("--rate", type=float, default=0.0,
                   help="Fixed Hz. 0 = pace by original sys_time deltas.")
    p.add_argument("--realtime", action="store_true",
                   help="실시간 동기화 모드: sensor CSV 의 모든 캡처 frame_id 를 원본 "
                        "sys_time 간격으로 송신(검출 0개는 empty detections). "
                        "--rate 가 0 일 때 의미 있음 (--rate > 0 이면 그 Hz 로 강제).")
    p.add_argument("--orientation",
                   choices=["landscape-left", "landscape-right"],
                   default="landscape-left",
                   help="realtime 모드에서 검출 없는 프레임의 cam fwd/up 추정에 사용.")
    p.add_argument("--loop", action="store_true",
                   help="Restart after finishing.")
    p.add_argument("--max-frames", type=int, default=0,
                   help="Stop after N frames (0 = unlimited).")
    args = p.parse_args()

    session = find_session(args.session)
    proj_csv = find_csv(os.path.join(session, "projection"), "projection_")
    print(f"[replay] {proj_csv}")

    frames, frame_time, frame_pose = load_projection(proj_csv)

    if args.realtime:
        # sensor CSV 를 머지해 모든 캡처 프레임을 시간순으로 enumerate.
        try:
            sensor_csv = find_csv(os.path.join(session, "sensor"), "sensor_data_")
        except SystemExit:
            print("[realtime] sensor CSV 없음 → 일반 모드로 폴백")
            sensor_idx = {}
        else:
            print(f"[sensor] {sensor_csv}")
            sensor_idx = load_sensor_index(sensor_csv)

        R_pc = (R_phone_cam_landscape_left() if args.orientation == "landscape-left"
                else R_phone_cam_landscape_right())

        # 모든 fid (projection ∪ sensor) 를 합치고, projection 에 없으면 빈 detections.
        all_fids = sorted(set(frames.keys()) | set(sensor_idx.keys()))
        n_added_empty = 0
        for fid in all_fids:
            if fid in frames:
                continue
            # projection 에 없던 프레임 — 빈 detection list + sensor 의 sys_time/quat 사용.
            s = sensor_idx.get(fid)
            if s is None:
                continue
            sys_time, quat = s
            frames[fid] = []
            frame_time[fid] = sys_time
            try:
                fwd, up = unity_camera_axes_from_quat(quat, R_pc)
                frame_pose[fid] = ([float(fwd[0]), float(fwd[1]), float(fwd[2])],
                                   [float(up[0]),  float(up[1]),  float(up[2])])
            except Exception:
                frame_pose[fid] = None
            n_added_empty += 1
        print(f"[realtime] +{n_added_empty} empty-detection frames merged from sensor")

    fids = sorted(frames.keys())
    n_det = sum(len(v) for v in frames.values())
    print(f"[replay] {len(fids)} frames, {n_det} detections")

    sender = UdpSender(args.host, args.port)
    if args.rate > 0:
        pacing = f"fixed {args.rate} Hz"
    elif args.realtime:
        pacing = "realtime (sys_time deltas, empty frames included)"
    else:
        pacing = "original timing (projection only)"
    print(f"[replay] UDP → {args.host}:{args.port}, pacing: {pacing}")

    period = (1.0 / args.rate) if args.rate > 0 else None

    sent_total = 0
    try:
        while True:
            t0 = time.monotonic()
            base_sys = frame_time[fids[0]]
            for i, fid in enumerate(fids):
                target = t0 + (i * period if period is not None
                               else (frame_time[fid] - base_sys))
                wait = target - time.monotonic()
                if wait > 0:
                    time.sleep(wait)
                pose = frame_pose.get(fid)
                fwd_p = pose[0] if pose else None
                up_p  = pose[1] if pose else None
                sender.send_frame(fid, frame_time[fid], frames[fid],
                                  cam_forward=fwd_p, cam_up=up_p)
                sent_total += 1
                print(f"  sent frame {fid}: {len(frames[fid])} detections   ",
                      end="\r", flush=True)
                if args.max_frames and sent_total >= args.max_frames:
                    print()
                    return
            print()
            if not args.loop:
                break
    except KeyboardInterrupt:
        print("\n[replay] interrupted")
    finally:
        sender.close()
        print(f"[replay] total packets sent: {sent_total}")


if __name__ == "__main__":
    main()
