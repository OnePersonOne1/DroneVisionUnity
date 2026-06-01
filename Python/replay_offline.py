"""Replay a saved projection_<session>.csv as UDP packets — same wire format
as live mode, so Unity's UDP receiver sees identical traffic.

Examples:
    python3 replay_offline.py                              # latest session, original timing
    python3 replay_offline.py --rate 10                    # fixed 10 Hz
    python3 replay_offline.py --host 192.168.0.42 --loop   # send to another machine, loop forever
"""

import argparse
import csv
import glob
import os
import time
from collections import defaultdict
from pathlib import Path

from udp_sender import UdpSender

PROJECT_ROOT = Path(__file__).resolve().parent.parent


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


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--session", default=None,
                   help="Session folder (default: latest under output/)")
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=9870)
    p.add_argument("--rate", type=float, default=0.0,
                   help="Fixed Hz. 0 = pace by original sys_time deltas.")
    p.add_argument("--loop", action="store_true",
                   help="Restart after finishing.")
    p.add_argument("--max-frames", type=int, default=0,
                   help="Stop after N frames (0 = unlimited).")
    args = p.parse_args()

    session = find_session(args.session)
    proj_csv = find_csv(os.path.join(session, "projection"), "projection_")
    print(f"[replay] {proj_csv}")

    frames, frame_time, frame_pose = load_projection(proj_csv)
    fids = sorted(frames.keys())
    n_det = sum(len(v) for v in frames.values())
    print(f"[replay] {len(fids)} frames, {n_det} detections")

    sender = UdpSender(args.host, args.port)
    pacing = f"fixed {args.rate} Hz" if args.rate > 0 else "original timing"
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
